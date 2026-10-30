using IntegrationTests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;

namespace IntegrationTests.Identity;

[Collection(KeycloakRealmDefinition.Name)]
public sealed class KeycloakApiTests(KeycloakRealm keycloak)
{
    [Fact]
    public async Task ApiValidatesKeycloakTokens()
    {
        // Emissor público na porta 8180 e metadados lidos pela porta mapeada do container, como na Api do compose.
        new Uri(keycloak.InternalMetadataAddress).Port.ShouldNotBe(8180);
        await using var api = IdentityApi.WithKeycloak(KeycloakRealm.PublicIssuer, keycloak.InternalMetadataAddress);
        using var client = api.CreateClient();
        var paymentsHub = (await keycloak.ClientCredentialsAsync("payments-hub")).AccessToken;
        var login = await keycloak.ChannelLoginAsync("ana");
        var ana = (await keycloak.ExchangeForLedgerApiAsync(login.AccessToken)).AccessToken;

        using var credit = await client.GetAsync(IdentityApi.CreditsRoute, paymentsHub);
        using var debit = await client.GetAsync(IdentityApi.DebitsRoute, paymentsHub);
        using var delegatedDebit = await client.GetAsync(IdentityApi.DebitsRoute, ana);
        var caller = await delegatedDebit.JsonAsync();

        ((int)credit.StatusCode).ShouldBe(200);
        await debit.ShouldBeInsufficientScopeAsync();
        ((int)delegatedDebit.StatusCode).ShouldBe(200);
        caller.GetProperty("mode").GetString().ShouldBe("Delegated");
        caller.GetProperty("customerId").GetString().ShouldBe("11111111-1111-4111-8111-111111111111");
    }

    [Fact]
    public async Task LoginTokenIsRefusedWithoutExchange()
    {
        await using var api = IdentityApi.WithKeycloak(KeycloakRealm.PublicIssuer, keycloak.InternalMetadataAddress);
        using var client = api.CreateClient();
        var login = await keycloak.ChannelLoginAsync("ana");
        var token = new JsonWebToken(login.AccessToken);

        using var response = await client.GetAsync(IdentityApi.DebitsRoute, login.AccessToken);

        login.Status.ShouldBe(200);
        token.Audiences.ShouldBe(["channel-app"]);
        token.GetClaim("scope").Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ShouldNotContain(scope => scope.StartsWith("ledger.", StringComparison.Ordinal) || scope.StartsWith("positions.", StringComparison.Ordinal) || scope.StartsWith("customers.", StringComparison.Ordinal));
        await response.ShouldBeAuthenticationRequiredAsync();
    }
}
