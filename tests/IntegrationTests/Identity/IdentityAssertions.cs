using System.Text.Json;

namespace IntegrationTests.Identity;

// Contrato dos erros de identidade: Problem Details (ADR-0042) com `WWW-Authenticate: Bearer`.
internal static class IdentityAssertions
{
    internal static Task<JsonElement> ShouldBeAuthenticationRequiredAsync(this HttpResponseMessage response) =>
        ShouldBeProblemAsync(response, 401, "authentication-required");

    internal static Task<JsonElement> ShouldBeInsufficientScopeAsync(this HttpResponseMessage response) =>
        ShouldBeProblemAsync(response, 403, "insufficient-scope");

    private static async Task<JsonElement> ShouldBeProblemAsync(HttpResponseMessage response, int status, string type)
    {
        var body = await response.JsonAsync();

        ((int)response.StatusCode).ShouldBe(status);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");
        response.Headers.WwwAuthenticate.ToString().ShouldStartWith("Bearer");
        body.GetProperty("type").GetString().ShouldBe(type);
        body.GetProperty("status").GetInt32().ShouldBe(status);
        body.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        body.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
        body.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        return body;
    }
}
