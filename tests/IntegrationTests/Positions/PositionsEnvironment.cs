extern alias ApiHost;

using IntegrationTests.Identity;
using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntegrationTests.Positions;

// PostgreSQL migrado e MongoDB reais, a Api real lendo o MongoDB com `positions_reader`, uma segunda Api com o MongoDB
// inalcançável (só o fallback responde) e o Worker real, iniciado pelo teste que precisa da projeção.
public sealed class PositionsEnvironment : IAsyncLifetime, ILedgerEnvironment
{
    // Polling curto: a projeção alcança o teste sem ele esperar o padrão de 1 s.
    internal const string PollInterval = "00:00:00.200";

    private WebApplicationFactory<ApiHost::Program> _api = null!;
    private WebApplicationFactory<ApiHost::Program> _fallbackApi = null!;

    internal PostgresDatabase Database { get; private set; } = null!;

    internal MongoReadModel Mongo { get; private set; } = null!;

    // Api com o MongoDB.
    internal HttpClient Client { get; private set; } = null!;

    // Api com o MongoDB inalcançável: toda consulta responde pelo Ledger.
    internal HttpClient FallbackClient { get; private set; } = null!;

    internal LedgerRows Rows { get; private set; } = null!;

    HttpClient ILedgerEnvironment.Client => Client;

    PostgresDatabase ILedgerEnvironment.Database => Database;

    internal LogSink ApiLog { get; } = new();

    public async ValueTask InitializeAsync()
    {
        var postgres = PostgresDatabase.StartAsync();
        var mongo = MongoReadModel.StartAsync();
        Database = await postgres;
        Mongo = await mongo;
        var migration = await MigratorProcess.RunAsync(Database.ConnectionStringFor("migrator"));
        migration.ExitCode.ShouldBe(0, migration.Output);

        _api = CreateApi(Mongo.ConnectionStringFor(MongoReadModel.Reader), ApiLog);
        _fallbackApi = CreateApi(UnreachableDatabase.MongoConnectionString, new LogSink());
        Client = _api.CreateClient();
        FallbackClient = _fallbackApi.CreateClient();
        Rows = new LedgerRows(Database);
        await WarmUpAsync();
    }

    // Também roda depois de uma subida que falhou no meio: só descarta o que chegou a existir.
    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        FallbackClient?.Dispose();
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        if (_fallbackApi is not null)
        {
            await _fallbackApi.DisposeAsync();
        }

        if (Mongo is not null)
        {
            await Mongo.DisposeAsync();
        }

        if (Database is not null)
        {
            await Database.DisposeAsync();
        }
    }

    internal WorkerProcess StartWorker() => WorkerProcess.Start(new Dictionary<string, string>
    {
        ["ConnectionStrings__Ledger"] = Database.ConnectionStringFor("ledger_worker"),
        ["ConnectionStrings__Positions"] = Mongo.ConnectionStringFor(MongoReadModel.Writer),
        ["Outbox__PollInterval"] = PollInterval,
    });

    // Espera a conta chegar à versão no MongoDB, com o Worker vivo.
    internal Task WaitProjectedAsync(WorkerProcess worker, Guid accountId, long version) =>
        Eventually.WaitUntilAsync(
            async () =>
            {
                worker.HasExited.ShouldBeFalse(worker.Output);
                var position = await Mongo.PositionAsync(accountId);
                return position is not null && position["version"].AsInt64 == version;
            },
            TimeSpan.FromSeconds(60),
            () => $"account {accountId} never reached version {version}\n{worker.Output}",
            TimeSpan.FromMilliseconds(100));

    // Espera todos os eventos da conta marcados: a versão no MongoDB avança antes da marcação, no mesmo lote.
    internal Task WaitProcessedAsync(WorkerProcess worker, Guid accountId) =>
        Eventually.WaitUntilAsync(
            async () =>
            {
                worker.HasExited.ShouldBeFalse(worker.Output);
                return (await Database.OutboxAsync(accountId)).TrueForAll(row => row.ProcessedAt != null);
            },
            TimeSpan.FromSeconds(60),
            () => $"events of {accountId} never processed\n{worker.Output}",
            TimeSpan.FromMilliseconds(100));

    // Projeta a conta até a versão atual dela, com os eventos marcados, e desliga o Worker.
    internal async Task ProjectAsync(Guid accountId)
    {
        var version = (await Rows.AccountAsync(accountId)).ShouldNotBeNull().Version;
        using var worker = StartWorker();
        await WaitProjectedAsync(worker, accountId, version);
        await WaitProcessedAsync(worker, accountId);
    }

    // As respostas do caminho do MongoDB e do fallback logadas pela Api desde a marca.
    internal IReadOnlyList<string> ReadPaths(int mark) =>
        ApiLog.Since(mark)
            .Where(line => line.Category.StartsWith("Positions.", StringComparison.Ordinal))
            .Select(line => line.Message)
            .ToList();

    // A primeira leitura abre conexão e autentica no MongoDB, o que pode passar do timeout: a Api começa aquecida.
    internal async Task WarmUpAsync()
    {
        var account = await this.OpenAccountAsync();
        await Eventually.WaitUntilAsync(
            async () =>
            {
                var mark = ApiLog.Mark();
                using var response = await Client.GetAsync($"/v1/accounts/{account.Id}/balance", LedgerTokens.Backoffice);
                return (int)response.StatusCode == 200 && ReadPaths(mark).Contains(PositionsLog.ServedFromReadModel);
            },
            TimeSpan.FromSeconds(30),
            () => "the Api never answered from the read model",
            TimeSpan.FromMilliseconds(100));
    }

    private WebApplicationFactory<ApiHost::Program> CreateApi(string positions, LogSink log) =>
        new WebApplicationFactory<ApiHost::Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("Identity:Issuer", TestTokenIssuer.Issuer)
            .UseSetting("ConnectionStrings:Ledger", Database.ConnectionStringFor("ledger_app", pooling: true))
            .UseSetting("ConnectionStrings:Customers", Database.ConnectionStringFor("customers_app", pooling: true))
            .WithVault()
            .UseSetting("ConnectionStrings:Positions", positions)
            .ConfigureLogging(logging => logging.AddProvider(log).AddFilter<LogSink>("Positions", LogLevel.Debug))
            .ConfigureTestServices(services =>
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    options.Configuration = TestTokenIssuer.Configuration)));
}

// Mensagens que a Api loga a cada consulta, dizendo qual caminho respondeu.
internal static class PositionsLog
{
    internal const string ServedFromReadModel = "Answering from the read model";
    internal const string Behind = "Read model behind the ledger; answering from the ledger";
    internal const string TimedOutPrefix = "Read model did not answer within";
    internal const string Failed = "Read model failed; answering from the ledger";
}

[CollectionDefinition(Name)]
public sealed class PositionsApiDefinition : ICollectionFixture<PositionsEnvironment>
{
    public const string Name = "positions-api";
}

[CollectionDefinition(Name)]
public sealed class PositionsProjectionDefinition : ICollectionFixture<PositionsEnvironment>
{
    public const string Name = "positions-projection";
}
