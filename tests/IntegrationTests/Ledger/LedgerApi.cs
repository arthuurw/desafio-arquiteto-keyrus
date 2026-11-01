extern alias ApiHost;

using IntegrationTests.Identity;
using IntegrationTests.Infrastructure;
using Ledger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntegrationTests.Ledger;

// A Api real contra PostgreSQL real migrado pelo Migrator, com tokens do emissor de teste.
public sealed class LedgerApi : IAsyncLifetime, ILedgerEnvironment
{
    private WebApplicationFactory<ApiHost::Program> _factory = null!;

    internal PostgresDatabase Database { get; private set; } = null!;

    internal HttpClient Client { get; private set; } = null!;

    internal LedgerRows Rows { get; private set; } = null!;

    internal IServiceProvider Services => _factory.Services;

    internal KeyReadInterceptor KeyReads { get; } = new();

    internal LogSink ApiLog { get; } = new();

    internal RequestCompletions Completions { get; } = new();

    HttpClient ILedgerEnvironment.Client => Client;

    PostgresDatabase ILedgerEnvironment.Database => Database;

    public async ValueTask InitializeAsync()
    {
        Database = await PostgresDatabase.StartAsync();
        var migration = await MigratorProcess.RunAsync(Database.ConnectionStringFor("migrator"));
        migration.ExitCode.ShouldBe(0, migration.Output);

        _factory = new WebApplicationFactory<ApiHost::Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("Identity:Issuer", TestTokenIssuer.Issuer)
            .UseSetting("ConnectionStrings:Ledger", Database.ConnectionStringFor("ledger_app", pooling: true))
            .UseSetting("ConnectionStrings:Customers", Database.ConnectionStringFor("customers_app", pooling: true))
            .WithVault()
            .UseSetting("ConnectionStrings:Positions", UnreachableDatabase.MongoConnectionString)
            .ConfigureLogging(logging => logging.AddProvider(ApiLog).AddFilter<LogSink>(null, LogLevel.Trace))
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(Completions);
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    options.Configuration = TestTokenIssuer.Configuration);
                services.ConfigureDbContext<LedgerDbContext>(options => options.AddInterceptors(KeyReads));
            }));
        Client = _factory.CreateClient();
        Rows = new LedgerRows(Database);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        await Database.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class LedgerApiDefinition : ICollectionFixture<LedgerApi>
{
    public const string Name = "ledger-api";
}
