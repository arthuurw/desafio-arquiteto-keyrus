using IntegrationTests.Identity;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class BalanceLimitTests(LedgerApi api)
{
    [Fact]
    public async Task CreditAboveMaxBalanceIsRefused()
    {
        var account = await api.OpenAccountAsync();
        await SetBalanceAsync(account.Id, "999999999999999.00");
        var before = await api.Rows.AccountAsync(account.Id);
        var key = Guid.NewGuid().ToString();
        var reference = LedgerClient.NewReference();

        using var refused = await api.Client.CreditAsync(account.Id, "1.00", externalReference: reference, idempotencyKey: key);
        await refused.ShouldBeProblemAsync(422, "balance-limit-exceeded");
        var afterRefusal = await api.Rows.AccountAsync(account.Id);
        var entriesAfterRefusal = await api.Rows.EntriesAsync(account.Id);
        using var repeated = await api.Client.CreditAsync(account.Id, "1.00", externalReference: reference, idempotencyKey: key);
        using var accepted = await api.Client.CreditAsync(account.Id, "0.99");

        refused.IsReplay().ShouldBeFalse();
        afterRefusal.ShouldBe(before);
        entriesAfterRefusal.ShouldBeEmpty();
        await repeated.ShouldBeProblemAsync(422, "balance-limit-exceeded");
        repeated.IsReplay().ShouldBeTrue();
        (await accepted.ShouldBeEntryAsync()).GetProperty("balanceAfter").GetString().ShouldBe("999999999999999.99");
        (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().Balance.ShouldBe(999999999999999.99m);
    }

    [Fact]
    public async Task ReversalOfDebitAboveMaxBalanceIsRefused()
    {
        var account = await api.OpenAccountAsync();
        await api.Client.CreditOkAsync(account.Id, "1.00");
        var debitId = (await api.Client.DebitOkAsync(account, "1.00")).GetProperty("id").GetGuid();
        await SetBalanceAsync(account.Id, "999999999999999.99");
        var before = await api.Rows.AccountAsync(account.Id);

        using var refused = await api.Client.ReverseAsync(account.Id, debitId);

        await refused.ShouldBeProblemAsync(422, "balance-limit-exceeded");
        (await api.Rows.AccountAsync(account.Id)).ShouldBe(before);
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(2);
    }

    // A Api aceita até 999999999999.99 por lançamento: o saldo perto do teto é posto direto na linha da conta.
    private Task SetBalanceAsync(Guid accountId, string balance) =>
        api.Database.ExecuteAsync("postgres", $"UPDATE ledger.accounts SET balance = {balance} WHERE id = '{accountId}'");
}
