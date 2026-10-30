using static IntegrationTests.Identity.TestTokenIssuer;

namespace IntegrationTests.Identity;

public sealed class AuthorizationTests
{
    [Fact]
    public async Task RouteRequiresItsExactScope()
    {
        await using var api = IdentityApi.WithTestIssuer();
        using var client = api.CreateClient();

        using var credits = await client.GetAsync(IdentityApi.DebitsRoute, Sign(Service("backoffice", "ledger.credits.write")));
        using var debits = await client.GetAsync(IdentityApi.DebitsRoute, Sign(Service("backoffice", "ledger.debits.write")));
        using var longer = await client.GetAsync(IdentityApi.DebitsRoute, Sign(Service("backoffice", "ledger.debits.write.all")));
        using var prefix = await client.GetAsync(IdentityApi.DebitsRoute, Sign(Service("backoffice", "ledger.debits")));

        await credits.ShouldBeInsufficientScopeAsync();
        ((int)debits.StatusCode).ShouldBe(200);
        await longer.ShouldBeInsufficientScopeAsync();
        await prefix.ShouldBeInsufficientScopeAsync();
    }

    [Fact]
    public async Task RouteWithoutPolicyIsDenied()
    {
        await using var api = IdentityApi.WithTestIssuer();
        using var client = api.CreateClient();

        using var anonymous = await client.GetAsync(IdentityApi.RouteWithoutPolicy, token: null);
        using var allScopes = await client.GetAsync(IdentityApi.RouteWithoutPolicy, Sign(Service("backoffice", AllScopes)));

        await anonymous.ShouldBeAuthenticationRequiredAsync();
        await allScopes.ShouldBeInsufficientScopeAsync();
    }

    [Fact]
    public async Task LivenessStaysAnonymous()
    {
        await using var api = IdentityApi.WithTestIssuer();
        using var client = api.CreateClient();

        using var anonymous = await client.GetAsync("/health/live", token: null);
        using var invalidToken = await client.GetAsync("/health/live", "not-a-token");

        ((int)anonymous.StatusCode).ShouldBe(200);
        (await anonymous.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Healthy");
        ((int)invalidToken.StatusCode).ShouldBe(200);
        (await invalidToken.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Healthy");
    }
}
