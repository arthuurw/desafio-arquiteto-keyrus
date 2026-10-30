using static IntegrationTests.Identity.TestTokenIssuer;

namespace IntegrationTests.Identity;

public sealed class AuthenticationTests
{
    private const string Scope = "ledger.debits.write";

    [Fact]
    public async Task MissingTokenIsRejected()
    {
        await using var api = IdentityApi.WithTestIssuer();
        using var client = api.CreateClient();

        using var response = await client.GetAsync(IdentityApi.DebitsRoute, token: null);

        await response.ShouldBeAuthenticationRequiredAsync();
    }

    [Fact]
    public async Task ForgedSignatureIsRejected()
    {
        await using var api = IdentityApi.WithTestIssuer();
        using var client = api.CreateClient();
        var claims = Service("backoffice", Scope);

        using var otherKey = await client.GetAsync(IdentityApi.DebitsRoute, SignWithOtherKey(claims));
        using var algNone = await client.GetAsync(IdentityApi.DebitsRoute, Unsigned(claims));
        using var hmacWithPublicKey = await client.GetAsync(IdentityApi.DebitsRoute, SignWithPublicKeyAsHmacSecret(claims));

        await otherKey.ShouldBeAuthenticationRequiredAsync();
        await algNone.ShouldBeAuthenticationRequiredAsync();
        await hmacWithPublicKey.ShouldBeAuthenticationRequiredAsync();
    }

    [Fact]
    public async Task WrongIssuerIsRejected()
    {
        await using var api = IdentityApi.WithTestIssuer();
        using var client = api.CreateClient();
        using var response = await client.GetAsync(IdentityApi.DebitsRoute, Sign(FromOtherIssuer()));

        await response.ShouldBeAuthenticationRequiredAsync();
    }

    [Fact]
    public async Task OnlyConfiguredIssuerIsAccepted()
    {
        const string configured = "https://configured.test/realms/ledger";
        await using var api = IdentityApi.WithTestIssuer(configuredIssuer: configured);
        using var client = api.CreateClient();
        var fromConfiguration = Service("backoffice", Scope);
        fromConfiguration["iss"] = configured;

        using var metadataIssuer = await client.GetAsync(IdentityApi.DebitsRoute, Sign(Service("backoffice", Scope)));
        using var configuredIssuer = await client.GetAsync(IdentityApi.DebitsRoute, Sign(fromConfiguration));

        TestTokenIssuer.Configuration.Issuer.ShouldNotBe(configured);
        await metadataIssuer.ShouldBeAuthenticationRequiredAsync();
        ((int)configuredIssuer.StatusCode).ShouldBe(200);
    }

    [Fact]
    public async Task AudienceMustBeLedgerApi()
    {
        await using var api = IdentityApi.WithTestIssuer();
        using var client = api.CreateClient();

        using var ledgerApi = await client.GetAsync(IdentityApi.DebitsRoute, Sign(WithAudience("ledger-api")));
        using var account = await client.GetAsync(IdentityApi.DebitsRoute, Sign(WithAudience("account")));
        using var similar = await client.GetAsync(IdentityApi.DebitsRoute, Sign(WithAudience("ledger-api-x")));

        ((int)ledgerApi.StatusCode).ShouldBe(200);
        await account.ShouldBeAuthenticationRequiredAsync();
        await similar.ShouldBeAuthenticationRequiredAsync();
    }

    [Fact]
    public async Task LifetimeAllowsThirtySecondsOfSkew()
    {
        await using var api = IdentityApi.WithTestIssuer();
        using var client = api.CreateClient();

        using var expired31 = await client.GetAsync(IdentityApi.DebitsRoute, Sign(Expired(TimeSpan.FromSeconds(31))));
        using var expired29 = await client.GetAsync(IdentityApi.DebitsRoute, Sign(Expired(TimeSpan.FromSeconds(29))));
        using var notBefore31 = await client.GetAsync(IdentityApi.DebitsRoute, Sign(NotYetValid(TimeSpan.FromSeconds(31))));
        using var notBefore29 = await client.GetAsync(IdentityApi.DebitsRoute, Sign(NotYetValid(TimeSpan.FromSeconds(29))));

        await expired31.ShouldBeAuthenticationRequiredAsync();
        ((int)expired29.StatusCode).ShouldBe(200);
        await notBefore31.ShouldBeAuthenticationRequiredAsync();
        ((int)notBefore29.StatusCode).ShouldBe(200);
    }

    [Fact]
    public async Task EveryFailureCauseSharesTheSameBody()
    {
        await using var api = IdentityApi.WithTestIssuer();
        using var client = api.CreateClient();

        using var missing = await client.GetAsync(IdentityApi.DebitsRoute, token: null);
        using var forged = await client.GetAsync(IdentityApi.DebitsRoute, SignWithOtherKey(Service("backoffice", Scope)));
        using var issuer = await client.GetAsync(IdentityApi.DebitsRoute, Sign(FromOtherIssuer()));
        using var expired = await client.GetAsync(IdentityApi.DebitsRoute, Sign(Expired(TimeSpan.FromMinutes(10))));

        var bodies = new[]
        {
            await missing.ShouldBeAuthenticationRequiredAsync(),
            await forged.ShouldBeAuthenticationRequiredAsync(),
            await issuer.ShouldBeAuthenticationRequiredAsync(),
            await expired.ShouldBeAuthenticationRequiredAsync(),
        };
        bodies.Select(body => body.GetProperty("title").GetString()).Distinct().Count().ShouldBe(1);
        bodies.Select(body => body.GetProperty("detail").GetString()).Distinct().Count().ShouldBe(1);
    }

    private static Dictionary<string, object> FromOtherIssuer()
    {
        var claims = Service("backoffice", Scope);
        claims["iss"] = "https://other-issuer.test/realms/ledger";
        return claims;
    }

    private static Dictionary<string, object> WithAudience(string audience)
    {
        var claims = Service("backoffice", Scope);
        claims["aud"] = new[] { audience };
        return claims;
    }

    // Token de 300 s cujo `exp` passou há `ago`.
    private static Dictionary<string, object> Expired(TimeSpan ago)
    {
        var claims = Service("backoffice", Scope);
        claims["iat"] = UnixSeconds(-ago - TimeSpan.FromSeconds(300));
        claims["nbf"] = UnixSeconds(-ago - TimeSpan.FromSeconds(300));
        claims["exp"] = UnixSeconds(-ago);
        return claims;
    }

    // Token de 300 s cujo `nbf` fica `ahead` no futuro.
    private static Dictionary<string, object> NotYetValid(TimeSpan ahead)
    {
        var claims = Service("backoffice", Scope);
        claims["iat"] = UnixSeconds(ahead);
        claims["nbf"] = UnixSeconds(ahead);
        claims["exp"] = UnixSeconds(ahead + TimeSpan.FromSeconds(300));
        return claims;
    }
}
