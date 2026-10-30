using IntegrationTests.Identity;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class AccountOpeningTests(LedgerApi api)
{
    [Fact]
    public async Task OpensAccountWithZeroBalanceAndLimit()
    {
        var customerId = Guid.NewGuid();

        using var first = await api.Client.SendJsonAsync(HttpMethod.Post, "/v1/accounts", new { customerId }, LedgerTokens.Backoffice);
        using var second = await api.Client.SendJsonAsync(HttpMethod.Post, "/v1/accounts", new { customerId }, LedgerTokens.Backoffice);
        var firstId = (await first.JsonAsync()).GetProperty("id").GetGuid();
        var secondId = (await second.JsonAsync()).GetProperty("id").GetGuid();
        var row = await api.Rows.AccountAsync(firstId);

        ((int)first.StatusCode).ShouldBe(201);
        ((int)second.StatusCode).ShouldBe(201);
        firstId.Version.ShouldBe(7);
        secondId.Version.ShouldBe(7);
        secondId.ShouldNotBe(firstId);
        row.ShouldNotBeNull();
        row.CustomerId.ShouldBe(customerId);
        row.Balance.ShouldBe(0m);
        row.CreditLimit.ShouldBe(0m);
        row.Version.ShouldBe(0);
        (await api.Rows.AccountAsync(secondId)).ShouldNotBeNull().CustomerId.ShouldBe(customerId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{ "customerId": "abc" }""")]
    [InlineData("""{ "customerId": "00000000-0000-0000-0000-000000000000" }""")]
    public async Task RejectsInvalidCustomerId(string body)
    {
        var before = await api.Rows.AccountCountAsync();

        using var response = await api.Client.SendRawAsync(HttpMethod.Post, "/v1/accounts", body, LedgerTokens.Backoffice);

        await response.ShouldBeValidationErrorAsync("customerId");
        (await api.Rows.AccountCountAsync()).ShouldBe(before);
    }
}
