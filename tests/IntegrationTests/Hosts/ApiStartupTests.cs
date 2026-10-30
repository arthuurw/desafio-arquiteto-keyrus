using IntegrationTests.Infrastructure;

namespace IntegrationTests.Hosts;

public sealed class ApiStartupTests
{
    private const string HttpMetadata = $"{KeycloakRealm.PublicIssuer}/.well-known/openid-configuration";

    [Fact]
    public async Task RefusesHttpMetadataOutsideDevelopment()
    {
        using var api = ApiProcess.Start("Production", HttpMetadata);

        var exitCode = await api.WaitForExitAsync(TimeSpan.FromSeconds(60));

        exitCode.ShouldNotBe(0, api.Output);
        api.Output.ShouldContain("Identity:MetadataAddress must use https outside Development.");
        api.Output.ShouldNotContain("Now listening on");
    }

    [Fact]
    public async Task AcceptsHttpMetadataInDevelopment()
    {
        using var api = ApiProcess.Start("Development", HttpMetadata);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{api.Port}") };

        var (status, body) = await PollLivenessAsync(api, client);

        status.ShouldBe(200, api.Output);
        body.ShouldBe("Healthy");
    }

    // Espera a Api aceitar conexões; desiste se o processo terminar ou após 60 s.
    private static async Task<(int Status, string Body)> PollLivenessAsync(ApiProcess api, HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!api.HasExited && DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
                return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }
            catch (HttpRequestException)
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
            }
        }

        return (0, string.Empty);
    }
}
