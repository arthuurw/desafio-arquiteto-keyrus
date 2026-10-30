// Api e Worker declaram ambos Program no namespace global; os aliases os distinguem.
extern alias ApiHost;
extern alias WorkerHost;

using IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IntegrationTests.Hosts;

public sealed class LivenessTests
{
    [Fact]
    public async Task ApiAnswersHealthyWithoutDatabase()
    {
        // Emissor inalcançável: liveness também não depende do provedor de identidade.
        await using var factory = new WebApplicationFactory<ApiHost::Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("Identity:Issuer", "https://127.0.0.1:1/realms/ledger")
            .UseSetting("ConnectionStrings:Ledger", UnreachableDatabase.ConnectionString)
            .UseSetting("ConnectionStrings:Customers", UnreachableDatabase.ConnectionString));

        await AssertHealthyAsync(factory);
    }

    [Fact]
    public async Task WorkerAnswersHealthyWithoutDatabase()
    {
        await using var factory = new WebApplicationFactory<WorkerHost::Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("ConnectionStrings:Ledger", UnreachableDatabase.ConnectionString));

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
