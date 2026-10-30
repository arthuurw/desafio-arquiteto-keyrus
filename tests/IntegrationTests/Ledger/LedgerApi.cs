extern alias ApiHost;

using IntegrationTests.Identity;
using IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests.Ledger;

// A Api real contra PostgreSQL real migrado pelo Migrator, com tokens do emissor de teste.
public sealed class LedgerApi : IAsyncLifetime
{
    private WebApplicationFactory<ApiHost::Program> _factory = null!;

    internal PostgresDatabase Database { get; private set; } = null!;

    internal HttpClient Client { get; private set; } = null!;

    internal LedgerRows Rows { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Database = await PostgresDatabase.StartAsync();
        var migration = await MigratorProcess.RunAsync(Database.ConnectionStringFor("migrator"));
        migration.ExitCode.ShouldBe(0, migration.Output);

        _factory = new WebApplicationFactory<ApiHost::Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("Identity:Issuer", TestTokenIssuer.Issuer)
            .UseSetting("ConnectionStrings:Ledger", Database.ConnectionStringFor("ledger_app", pooling: true))
            .UseSetting("ConnectionStrings:Customers", UnreachableDatabase.ConnectionString)
            .ConfigureTestServices(services =>
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    options.Configuration = TestTokenIssuer.Configuration)));
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
