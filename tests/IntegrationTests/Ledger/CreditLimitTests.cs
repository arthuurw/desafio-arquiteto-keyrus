namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class CreditLimitTests(LedgerApi api)
{
    [Fact]
    public async Task LimitAllowsDebitDownToMinusLimit()
    {
        var account = await api.Client.OpenAccountAsync();
        var fresh = await api.Client.OpenAccountAsync();
        await api.Client.SetCreditLimitOkAsync(fresh.Id, "100.00");
        var freshBefore = await api.Rows.AccountAsync(fresh.Id);

        using var change = await api.Client.SetCreditLimitAsync(account.Id, "500.00");
        var afterChange = await api.Rows.AccountAsync(account.Id);
        using var debit = await api.Client.DebitAsync(account, "500.00");
        using var beyond = await api.Client.DebitAsync(account, "0.01");
        using var zero = await api.Client.SetCreditLimitAsync(fresh.Id, "0");

        ((int)change.StatusCode).ShouldBe(204);
        afterChange.ShouldNotBeNull().CreditLimit.ShouldBe(500m);
        afterChange.Version.ShouldBe(0);
        (await debit.ShouldBeEntryAsync()).GetProperty("balanceAfter").GetString().ShouldBe("-500.00");
        await beyond.ShouldBeProblemAsync(422, "insufficient-funds");
        freshBefore.ShouldNotBeNull().CreditLimit.ShouldBe(100m);
        ((int)zero.StatusCode).ShouldBe(204);
        (await api.Rows.AccountAsync(fresh.Id)).ShouldNotBeNull().CreditLimit.ShouldBe(0m);
    }

    [Theory]
    [InlineData("""{ "creditLimit": "-1.00" }""")]
    [InlineData("""{ "creditLimit": "10.001" }""")]
    [InlineData("""{ "creditLimit": 10 }""")]
    public async Task RejectsInvalidCreditLimit(string body)
    {
        var account = await api.Client.OpenAccountAsync();
        await api.Client.SetCreditLimitOkAsync(account.Id, "100.00");

        using var response = await api.Client.SendRawAsync(HttpMethod.Put, $"/v1/accounts/{account.Id}/credit-limit", body, LedgerTokens.Backoffice);

        await response.ShouldBeValidationErrorAsync("creditLimit");
        (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().CreditLimit.ShouldBe(100m);
    }

    [Fact]
    public async Task RejectsLimitBelowUsedBalance()
    {
        var account = await api.Client.OpenAccountAsync();
        await api.Client.SetCreditLimitOkAsync(account.Id, "300.00");
        await api.Client.DebitOkAsync(account, "300.00");

        using var below = await api.Client.SetCreditLimitAsync(account.Id, "299.99");
        var afterRefusal = await api.Rows.AccountAsync(account.Id);
        using var exact = await api.Client.SetCreditLimitAsync(account.Id, "300.00");

        await below.ShouldBeProblemAsync(422, "credit-limit-below-balance");
        afterRefusal.ShouldNotBeNull().CreditLimit.ShouldBe(300m);
        afterRefusal.Balance.ShouldBe(-300m);
        ((int)exact.StatusCode).ShouldBe(204);
    }
}
