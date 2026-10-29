extern alias ApiHost;
extern alias WorkerHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IntegrationTests.Hosts;

public sealed class LivenessTests
{
    // Porta 1: nenhum PostgreSQL escuta ali. Liveness não pode depender do banco (ADR-0021).
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=ledger_db;Username=ledger_app;Password=ledger_app_dev";

    [Fact]
    public async Task ApiAnswersHealthyWithoutDatabase()
    {
        await using var factory = new WebApplicationFactory<ApiHost::Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("ConnectionStrings:Ledger", UnreachableDatabase)
            .UseSetting("ConnectionStrings:Customers", UnreachableDatabase));

        await AssertHealthyAsync(factory);
    }

    [Fact]
    public async Task WorkerAnswersHealthyWithoutDatabase()
    {
        await using var factory = new WebApplicationFactory<WorkerHost::Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("ConnectionStrings:Ledger", UnreachableDatabase));

        await AssertHealthyAsync(factory);
    }

    private static async Task AssertHealthyAsync<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(200);
        body.ShouldBe("Healthy");
    }
}
