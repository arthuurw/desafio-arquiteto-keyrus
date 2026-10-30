using System.Text.Json;
using IntegrationTests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;

namespace IntegrationTests.Identity;

[Collection(KeycloakRealmDefinition.Name)]
public sealed class KeycloakRealmTests(KeycloakRealm keycloak)
{
    [Fact]
    public void TestContainerMatchesCompose()
    {
        var service = ComposeService("keycloak");

        service.ShouldContain($"image: {KeycloakRealm.Image}");
        service.ShouldContain("- ./docker/keycloak/realm-ledger.json:/opt/keycloak/data/import/realm-ledger.json:ro");
        Path.GetFullPath(Path.Combine(Repository.Root, "docker/keycloak/realm-ledger.json")).ShouldBe(Repository.KeycloakRealmFile);
    }

    [Fact]
    public void PublicIssuerMatchesAcrossConfiguration()
    {
        var keycloakService = ComposeService("keycloak");
        var apiService = ComposeService("api");
        var development = JsonDocument.Parse(File.ReadAllText(Repository.ApiDevelopmentSettings)).RootElement;

        keycloakService.Split('\n').ShouldContain($"KC_HOSTNAME: {KeycloakRealm.PublicOrigin}");
        apiService.Split('\n').ShouldContain($"Identity__Issuer: {KeycloakRealm.PublicIssuer}");
        development.GetProperty("Identity").GetProperty("Issuer").GetString().ShouldBe(KeycloakRealm.PublicIssuer);
    }

    [Theory]
    [InlineData("backoffice", "ledger.accounts.manage ledger.entries.reverse customers.write customers.read positions.read")]
    [InlineData("compliance", "customers.pii.read customers.erasure.manage positions.read")]
    [InlineData("payments-hub", "ledger.credits.write")]
    public async Task ServiceClientsReceiveExactlyTheirScopes(string clientId, string scopes)
    {
        var response = await keycloak.ClientCredentialsAsync(clientId);
        var token = new JsonWebToken(response.AccessToken);

        response.Status.ShouldBe(200);
        token.Audiences.ShouldContain("ledger-api");
        token.GetClaim("client_id").Value.ShouldBe(clientId);
        (token.ValidTo - token.IssuedAt).TotalSeconds.ShouldBe(300);
        token.Scopes().ShouldBe(scopes.Split(' ').Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ChannelAppCannotUseClientCredentials()
    {
        var response = await keycloak.ClientCredentialsAsync("channel-app");

        response.Status.ShouldBe(401);
        response.Body.GetProperty("error").GetString().ShouldBe("unauthorized_client");
        response.Body.TryGetProperty("access_token", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ChannelAppCannotLogUsersIn()
    {
        var response = await keycloak.ChannelAppLoginAsync("ana");

        response.Status.ShouldBe(400);
        response.Body.GetProperty("error").GetString().ShouldBe("unauthorized_client");
        response.Body.TryGetProperty("access_token", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task UserCannotChangeOwnCustomerId()
    {
        var update = await keycloak.UpdateOwnAttributeAsync("ana", "customer_id", TestCustomers.Bruno);
        var profile = await keycloak.UserProfileAsync();
        var login = await keycloak.ChannelWebLoginAsync("ana");
        var exchange = await keycloak.ExchangeForLedgerApiAsync(login.AccessToken);

        update.ShouldBe(401);
        profile.GetProperty("unmanagedAttributePolicy").GetString().ShouldBe("ADMIN_EDIT");
        new JsonWebToken(exchange.AccessToken).GetClaim("customer_id").Value.ShouldBe(TestCustomers.Ana);
    }

    [Fact]
    public async Task ExchangedTokenCarriesCustomer()
    {
        var login = await keycloak.ChannelWebLoginAsync("ana");
        var exchange = await keycloak.ExchangeForLedgerApiAsync(login.AccessToken);
        var token = new JsonWebToken(exchange.AccessToken);

        login.Status.ShouldBe(200);
        new JsonWebToken(login.AccessToken).Audiences.ShouldNotContain("ledger-api");
        exchange.Status.ShouldBe(200);
        token.Audiences.ShouldContain("ledger-api");
        token.GetClaim("azp").Value.ShouldBe("channel-app");
        token.GetClaim("customer_id").Value.ShouldBe(TestCustomers.Ana);
        token.TryGetClaim("client_id", out _).ShouldBeFalse();
        (token.ValidTo - token.IssuedAt).TotalSeconds.ShouldBe(300);
        token.Scopes().ShouldNotBeEmpty();
        token.Scopes().ShouldBeSubsetOf(["ledger.debits.write", "positions.read", "customers.read"]);
    }

    [Fact]
    public async Task ExchangeCannotGainScopesOutsideChannelApp()
    {
        var login = await keycloak.ChannelWebLoginAsync("ana");
        var exchange = await keycloak.ExchangeForLedgerApiAsync(login.AccessToken, "ledger.debits.write ledger.credits.write");

        login.Status.ShouldBe(200);
        exchange.Status.ShouldBe(400);
        exchange.Body.GetProperty("error").GetString().ShouldBe("invalid_scope");
        exchange.Body.TryGetProperty("access_token", out _).ShouldBeFalse();
    }

    // Bloco do serviço no docker-compose.yml: da linha `  <nome>:` até o próximo serviço.
    private static string ComposeService(string name)
    {
        var lines = File.ReadAllLines(Repository.ComposeFile);
        var start = Array.IndexOf(lines, $"  {name}:");
        start.ShouldBeGreaterThanOrEqualTo(0, $"service {name} not found in docker-compose.yml");
        var block = lines.Skip(start + 1)
            .TakeWhile(line => line.Length == 0 || line.StartsWith("    ", StringComparison.Ordinal))
            .Select(line => line.Trim());
        return string.Join('\n', block);
    }
}
