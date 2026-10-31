using IntegrationTests.Identity;
using IntegrationTests.Ledger;

namespace IntegrationTests.Hosts;

// Banco inacessível: a escrita lança exceção do driver, que nenhuma rota trata.
public sealed class ErrorResponseTests
{
    [Fact]
    public async Task UnhandledExceptionIsInternalError()
    {
        var body = await CreditWithUnreachableDatabaseAsync("Production");

        body.ShouldNotContain("Npgsql");
        body.ShouldNotContain("Exception");
        body.ShouldNotContain(" at ");
    }

    [Fact]
    public async Task UnhandledExceptionIsInternalErrorInDevelopment()
    {
        var body = await CreditWithUnreachableDatabaseAsync("Development");

        // Sem a página de exceção do ASP.NET, que traria o tipo da exceção e o stack trace.
        body.ShouldNotContain("Npgsql");
        body.ShouldNotContain("Exception");
        body.ShouldNotContain(" at ");
    }

    private static async Task<string> CreditWithUnreachableDatabaseAsync(string environment)
    {
        await using var api = IdentityApi.WithTestIssuer(environment: environment);
        using var client = api.CreateClient();

        using var response = await client.CreditAsync(Guid.NewGuid(), "10.00");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = await response.ShouldBeProblemAsync(500, "internal-error");

        body.GetProperty("title").GetString().ShouldBe("Erro interno");
        return text;
    }
}
