namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class LedgerInvariantTests(LedgerApi api)
{
    [Fact]
    public async Task MixedOperationsKeepInvariants()
    {
        var account = await api.Client.OpenAccountAsync();
        await api.Client.SetCreditLimitOkAsync(account.Id, "200.00");

        var first = (await api.Client.CreditOkAsync(account.Id, "150.75")).GetProperty("id").GetGuid();
        var debit = (await api.Client.DebitOkAsync(account, "300.00")).GetProperty("id").GetGuid();
        using var refused = await api.Client.DebitAsync(account, "50.76");
        await api.Client.CreditOkAsync(account.Id, "0.01");
        using var debitReversal = await api.Client.ReverseAsync(account.Id, debit);
        using var creditReversal = await api.Client.ReverseAsync(account.Id, first);
        await api.Client.DebitOkAsync(account, "10.10");
        await api.Client.SetCreditLimitOkAsync(account.Id, "20.00");
        await api.Client.CreditOkAsync(account.Id, "5.05");

        await refused.ShouldBeProblemAsync(422, "insufficient-funds");
        await debitReversal.ShouldBeEntryAsync();
        await creditReversal.ShouldBeEntryAsync();
        await api.Rows.ShouldKeepInvariantsAsync(account.Id);
        var entries = await api.Rows.EntriesAsync(account.Id);
        entries.Count.ShouldBe(7);
        entries[^1].BalanceAfter.ShouldBe(-5.04m);
    }
}
