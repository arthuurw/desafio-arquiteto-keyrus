using System.Text.Json;
using static IntegrationTests.Identity.TestTokenIssuer;

namespace IntegrationTests.Identity;

public sealed class CallerTests
{
    private const string Ana = "11111111-1111-4111-8111-111111111111";
    private const string Scope = "ledger.debits.write";

    [Fact]
    public async Task DelegatedTokenResolvesCustomer()
    {
        await using var api = IdentityApi.WithTestIssuer();
        using var client = api.CreateClient();

        using var response = await client.GetAsync(IdentityApi.DebitsRoute, Sign(Delegated(Ana, Scope)));
        var caller = await response.JsonAsync();

        ((int)response.StatusCode).ShouldBe(200);
        caller.GetProperty("mode").GetString().ShouldBe("Delegated");
        caller.GetProperty("clientId").GetString().ShouldBe("channel-app");
        caller.GetProperty("customerId").GetString().ShouldBe(Ana);
    }

    [Fact]
    public async Task ServiceTokenResolvesClient()
    {
        await using var api = IdentityApi.WithTestIssuer();
        using var client = api.CreateClient();

        using var response = await client.GetAsync(IdentityApi.DebitsRoute, Sign(Service("backoffice", Scope)));
        var caller = await response.JsonAsync();

        ((int)response.StatusCode).ShouldBe(200);
        caller.GetProperty("mode").GetString().ShouldBe("Service");
        caller.GetProperty("clientId").GetString().ShouldBe("backoffice");
        caller.GetProperty("customerId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task AmbiguousCallerIsDenied()
    {
        await using var api = IdentityApi.WithTestIssuer();
        using var client = api.CreateClient();
        var both = Delegated(Ana, Scope);
        both["client_id"] = "channel-app";
        var neither = Service("backoffice", Scope);
        neither.Remove("client_id");
        var notUuid = Delegated("abc", Scope);

        using var bothResponse = await client.GetAsync(IdentityApi.DebitsRoute, Sign(both));
        using var neitherResponse = await client.GetAsync(IdentityApi.DebitsRoute, Sign(neither));
        using var notUuidResponse = await client.GetAsync(IdentityApi.DebitsRoute, Sign(notUuid));

        await bothResponse.ShouldBeInsufficientScopeAsync();
        await neitherResponse.ShouldBeInsufficientScopeAsync();
        await notUuidResponse.ShouldBeInsufficientScopeAsync();
    }
}
