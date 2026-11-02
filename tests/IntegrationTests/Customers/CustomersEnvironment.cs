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

namespace IntegrationTests.Customers;

// PostgreSQL migrado pelo Migrator, OpenBao real configurado pelo bootstrap do compose e a Api real com os dois, com
// tokens do emissor de teste. Os testes da coleção rodam um de cada vez: pausar o cofre não atravessa outro teste.
public sealed class CustomersEnvironment : IAsyncLifetime, ILedgerEnvironment
{
    private readonly List<WebApplicationFactory<ApiHost::Program>> _apis = [];
    private readonly SemaphoreSlim _ana = new(1, 1);
    private AnaRegistration? _anaRegistration;

    internal PostgresDatabase Database { get; private set; } = null!;

    internal OpenBaoVault Vault { get; private set; } = null!;

    internal HttpClient Client { get; private set; } = null!;

    internal LogSink ApiLog { get; } = new();

    internal RequestCompletions Completions { get; } = new();

    HttpClient ILedgerEnvironment.Client => Client;

    PostgresDatabase ILedgerEnvironment.Database => Database;

    public async ValueTask InitializeAsync()
    {
        var postgres = PostgresDatabase.StartAsync();
        var vault = OpenBaoVault.StartAsync();
        Database = await postgres;
        Vault = await vault;
        var migration = await MigratorProcess.RunAsync(Database.ConnectionStringFor("migrator"));
        migration.ExitCode.ShouldBe(0, migration.Output);
        Client = CreateClient(ApiLog);
        await WarmUpAsync();
    }

    // O primeiro cadastro faz o login do AppRole e abre as conexões, o que pode passar do timeout de 1 s do cofre: a
    // Api começa aquecida.
    internal async Task WarmUpAsync() =>
        await Eventually.WaitUntilAsync(
            async () =>
            {
                using var response = await Client.RegisterAsync(CustomersClient.NewCustomer());
                return (int)response.StatusCode == 201;
            },
            TimeSpan.FromSeconds(30),
            () => "the Api never registered a customer",
            TimeSpan.FromMilliseconds(200));

    // O cadastro de `CustomersClient.Ana`, feito uma vez pelo primeiro teste que o pede: o CPF fixo só entra uma vez.
    internal async Task<AnaRegistration> AnaAsync()
    {
        await _ana.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (_anaRegistration is null)
            {
                var key = Guid.NewGuid();
                using var response = await Client.RegisterAsync(CustomersClient.Ana, key.ToString());
                var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                ((int)response.StatusCode).ShouldBe(201, body);
                _anaRegistration = new AnaRegistration(
                    key, (int)response.StatusCode, body, response.IsReplay(), System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid());
            }

            return _anaRegistration;
        }
        finally
        {
            _ana.Release();
        }
    }

    // Uma Api a mais sobre o mesmo banco: outro endereço de cofre, outro TTL do cache, outro ambiente, ou só um cache
    // vazio.
    internal HttpClient CreateClient(LogSink log, string? vaultAddress = null, string? dekCacheTtl = null, string hostEnvironment = "Production")
    {
        var factory = new WebApplicationFactory<ApiHost::Program>().WithWebHostBuilder(builder =>
        {
            builder
                .UseEnvironment(hostEnvironment)
                .UseSetting("Identity:Issuer", TestTokenIssuer.Issuer)
                .UseSetting("ConnectionStrings:Ledger", Database.ConnectionStringFor("ledger_app", pooling: true))
                .UseSetting("ConnectionStrings:Customers", Database.ConnectionStringFor("customers_app", pooling: true))
                .UseSetting("ConnectionStrings:Positions", UnreachableDatabase.MongoConnectionString)
                .WithVault(vaultAddress ?? Vault.Address)
                .ConfigureLogging(logging => logging.AddProvider(log).AddFilter<LogSink>(null, LogLevel.Trace))
                .ConfigureTestServices(services =>
                {
                    services.AddSingleton<IStartupFilter>(Completions);
                    services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                        options.Configuration = TestTokenIssuer.Configuration);
                });
            if (dekCacheTtl is not null)
            {
                builder.UseSetting("Customers:DekCache:Ttl", dekCacheTtl);
            }
        });
        lock (_apis)
        {
            _apis.Add(factory);
        }

        return factory.CreateClient();
    }

    // Também roda depois de uma subida que falhou no meio: só descarta o que chegou a existir.
    public async ValueTask DisposeAsync()
    {
        _ana.Dispose();
        Client?.Dispose();
        foreach (var api in _apis)
        {
            await api.DisposeAsync();
        }

        if (Vault is not null)
        {
            await Vault.DisposeAsync();
        }

        if (Database is not null)
        {
            await Database.DisposeAsync();
        }
    }
}

// A resposta do cadastro da Ana, como o primeiro envio a recebeu.
internal sealed record AnaRegistration(Guid Key, int Status, string Body, bool Replayed, Guid Id);

// Sozinha, depois das coleções paralelas: o cofre responde em bem menos de 1 s, mas não com o Docker disputado por
// todos os containers da suíte ao mesmo tempo.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CustomersEnvironmentDefinition : ICollectionFixture<CustomersEnvironment>
{
    public const string Name = "customers";
}
