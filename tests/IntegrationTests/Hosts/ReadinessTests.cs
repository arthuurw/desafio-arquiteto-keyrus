extern alias ApiHost;
extern alias WorkerHost;

using IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IntegrationTests.Hosts;

// Readiness com a semântica do ADR-0021: a Api depende do PostgreSQL e do OpenBao, e o MongoDB fora a deixa só
// `Degraded`; o Worker depende do PostgreSQL e do MongoDB.
[Collection(ReadinessEnvironmentDefinition.Name)]
public sealed class ReadinessTests(ReadinessEnvironment environment)
{
    [Fact]
    public async Task ApiIsReadyWithEveryDependency()
    {
        await using var api = environment.Api();

        await ShouldEventuallyAnswerAsync(api.CreateClient(), 200, "Healthy");
    }

    [Fact]
    public async Task ApiStaysReadyAndDegradedWithoutTheReadModel()
    {
        await using var api = environment.Api();
        using var client = api.CreateClient();
        await ShouldEventuallyAnswerAsync(client, 200, "Healthy");

        await using (await environment.Mongo.PauseAsync())
        {
            await ShouldEventuallyAnswerAsync(client, 200, "Degraded");
        }
    }

    [Fact]
    public async Task ApiIsNotReadyWithoutTheVault()
    {
        await using var api = environment.Api(vaultAddress: UnreachableVault.Address);

        await ShouldAnswerAsync(api.CreateClient(), 503, "Unhealthy");
    }

    // O cofre selado responde, mas com `503`: não serve DEK nem HMAC.
    [Fact]
    public async Task ApiIsNotReadyWithASealedVault()
    {
        await using var sealedVault = new SealedVault();
        await sealedVault.StartAsync();
        await using var api = environment.Api(vaultAddress: sealedVault.Address);

        await ShouldAnswerAsync(api.CreateClient(), 503, "Unhealthy");
        sealedVault.Requests.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ApiIsNotReadyWithoutTheDatabase()
    {
        await using var api = environment.Api(ledgerConnection: UnreachableDatabase.ConnectionString);

        await ShouldAnswerAsync(api.CreateClient(), 503, "Unhealthy");
    }

    [Fact]
    public async Task WorkerIsReadyWithBothDatabases()
    {
        await using var worker = environment.Worker();

        await ShouldEventuallyAnswerAsync(worker.CreateClient(), 200, "Healthy");
    }

    [Fact]
    public async Task WorkerIsNotReadyWithoutTheReadModel()
    {
        await using var worker = environment.Worker(mongoConnection: UnreachableDatabase.MongoConnectionString);

        await ShouldAnswerAsync(worker.CreateClient(), 503, "Unhealthy");
    }

    private static async Task ShouldAnswerAsync(HttpClient client, int status, string body)
    {
        var (actualStatus, actualBody) = await ReadyAsync(client);

        (actualStatus, actualBody).ShouldBe((status, body));
    }

    // Com todas as dependências de pé, o primeiro acesso abre conexões e faz o login no cofre: sob a suíte inteira ele
    // pode passar dos 2 s da readiness, e o estado esperado chega na tentativa seguinte.
    private static async Task ShouldEventuallyAnswerAsync(HttpClient client, int status, string body)
    {
        var last = (Status: 0, Body: "");
        await Eventually.WaitUntilAsync(
            async () =>
            {
                last = await ReadyAsync(client);
                return last == (status, body);
            },
            TimeSpan.FromSeconds(30),
            () => $"/health/ready answered {last.Status} {last.Body}, expected {status} {body}",
            TimeSpan.FromMilliseconds(200));
    }

    private static async Task<(int Status, string Body)> ReadyAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}

// PostgreSQL migrado, MongoDB com os usuários do compose e OpenBao configurado pelo bootstrap: Api e Worker sobem contra
// eles, ou contra um endereço fora do ar no lugar de um.
public sealed class ReadinessEnvironment : IAsyncLifetime
{
    private PostgresDatabase? _database;
    private OpenBaoVault? _vault;

    internal MongoReadModel Mongo { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var database = PostgresDatabase.StartAsync();
        var mongo = MongoReadModel.StartAsync();
        var vault = OpenBaoVault.StartAsync();
        _database = await database;
        Mongo = await mongo;
        _vault = await vault;
        var migration = await MigratorProcess.RunAsync(_database.ConnectionStringFor("migrator"));
        migration.ExitCode.ShouldBe(0, migration.Output);
    }

    internal WebApplicationFactory<ApiHost::Program> Api(string? vaultAddress = null, string? ledgerConnection = null) =>
        new WebApplicationFactory<ApiHost::Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            // Readiness não depende do provedor de identidade: o emissor fica fora do ar.
            .UseSetting("Identity:Issuer", "https://127.0.0.1:1/realms/ledger")
            .UseSetting("ConnectionStrings:Ledger", ledgerConnection ?? _database!.ConnectionStringFor("ledger_app", pooling: true))
            .UseSetting("ConnectionStrings:Customers", _database!.ConnectionStringFor("customers_app", pooling: true))
            .UseSetting("ConnectionStrings:Positions", Mongo.ConnectionStringFor(MongoReadModel.Reader))
            .WithVault(vaultAddress ?? _vault!.Address, TestTimeouts.Budget));

    internal WebApplicationFactory<WorkerHost::Program> Worker(string? mongoConnection = null) =>
        new WebApplicationFactory<WorkerHost::Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("ConnectionStrings:Ledger", _database!.ConnectionStringFor("ledger_worker"))
            .UseSetting("ConnectionStrings:Customers", _database.ConnectionStringFor("customers_worker"))
            .UseSetting("ConnectionStrings:Positions", mongoConnection ?? Mongo.ConnectionStringFor(MongoReadModel.Writer)));

    // Também roda depois de uma subida que falhou no meio: só descarta o que chegou a existir.
    public async ValueTask DisposeAsync()
    {
        if (Mongo is not null)
        {
            await Mongo.DisposeAsync();
        }

        if (_vault is not null)
        {
            await _vault.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}

// Sozinha, depois das coleções paralelas: a readiness tem 2 s por dependência, e o Docker disputado pela suíte inteira
// pode passar disso.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReadinessEnvironmentDefinition : ICollectionFixture<ReadinessEnvironment>
{
    public const string Name = "readiness";
}
