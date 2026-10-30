using IntegrationTests.Identity;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class LedgerScopeTests(LedgerApi api)
{
    public static TheoryData<string, string> WrongClientPerRoute => new()
    {
        { "debits", "payments-hub" },
        { "credits", "channel-app" },
        { "debits", "backoffice" },
        { "reversal", "channel-app" },
        { "opening", "payments-hub" },
        { "credit-limit", "payments-hub" },
    };

    [Theory]
    [MemberData(nameof(WrongClientPerRoute))]
    public async Task EachRouteRequiresItsScope(string route, string client)
    {
        var account = await api.Client.OpenAccountAsync();
        var entryId = (await api.Client.CreditOkAsync(account.Id, "100.00")).GetProperty("id").GetGuid();
        var token = client switch
        {
            "payments-hub" => LedgerTokens.PaymentsHub,
            "channel-app" => LedgerTokens.Channel(account.CustomerId),
            _ => LedgerTokens.Backoffice,
        };
        var accountsBefore = await api.Rows.AccountCountAsync();
        var entriesBefore = await api.Rows.EntryCountAsync();
        var rowBefore = await api.Rows.AccountAsync(account.Id);

        using var anonymous = await SendAsync(route, account, entryId, token: null);
        using var forbidden = await SendAsync(route, account, entryId, token);

        await anonymous.ShouldBeAuthenticationRequiredAsync();
        await forbidden.ShouldBeInsufficientScopeAsync();
        (await api.Rows.AccountCountAsync()).ShouldBe(accountsBefore);
        (await api.Rows.EntryCountAsync()).ShouldBe(entriesBefore);
        (await api.Rows.AccountAsync(account.Id)).ShouldBe(rowBefore);
    }

    private Task<HttpResponseMessage> SendAsync(string route, TestAccount account, Guid entryId, string? token) => route switch
    {
        "opening" => api.Client.SendJsonAsync(HttpMethod.Post, "/v1/accounts", new { customerId = Guid.NewGuid() }, token),
        "credit-limit" => api.Client.SendJsonAsync(HttpMethod.Put, $"/v1/accounts/{account.Id}/credit-limit", new { creditLimit = "50.00" }, token),
        "credits" => api.Client.SendJsonAsync(
            HttpMethod.Post, $"/v1/accounts/{account.Id}/credits", new { amount = "1.00", category = "PIX_IN", externalReference = "R-1" }, token),
        "debits" => api.Client.SendJsonAsync(
            HttpMethod.Post, $"/v1/accounts/{account.Id}/debits", new { amount = "1.00", category = "PIX_OUT", externalReference = "R-1" }, token),
        _ => api.Client.SendJsonAsync(
            HttpMethod.Post, $"/v1/accounts/{account.Id}/entries/{entryId}/reversal", new { externalReference = "R-1" }, token),
    };
}
