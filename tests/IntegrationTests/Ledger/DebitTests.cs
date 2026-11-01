namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class DebitTests(LedgerApi api)
{
    [Fact]
    public async Task DebitWithinLimitIsAccepted()
    {
        var account = await api.OpenAccountAsync();
        await api.Client.CreditOkAsync(account.Id, "100.00");

        using var response = await api.Client.DebitAsync(account, "100.00", "PIX_OUT");
        var entry = await response.ShouldBeEntryAsync();

        entry.GetProperty("type").GetString().ShouldBe("debit");
        entry.GetProperty("amount").GetString().ShouldBe("100.00");
        entry.GetProperty("balanceAfter").GetString().ShouldBe("0.00");
        (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().Balance.ShouldBe(0m);
    }

    [Fact]
    public async Task DebitBeyondLimitIsRefused()
    {
        var account = await api.OpenAccountAsync();
        await api.Client.CreditOkAsync(account.Id, "100.00");

        using var beyond = await api.Client.DebitAsync(account, "100.01");
        var afterRefusal = await api.Rows.AccountAsync(account.Id);
        var entriesAfterRefusal = await api.Rows.EntriesAsync(account.Id);
        await api.Client.SetCreditLimitOkAsync(account.Id, "50.00");
        using var downToLimit = await api.Client.DebitAsync(account, "150.00");
        using var oneCentMore = await api.Client.DebitAsync(account, "0.01");

        await beyond.ShouldBeProblemAsync(422, "insufficient-funds");
        afterRefusal.ShouldNotBeNull().Balance.ShouldBe(100m);
        afterRefusal.Version.ShouldBe(1);
        entriesAfterRefusal.Count.ShouldBe(1);
        (await downToLimit.ShouldBeEntryAsync()).GetProperty("balanceAfter").GetString().ShouldBe("-50.00");
        await oneCentMore.ShouldBeProblemAsync(422, "insufficient-funds");
        (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().Balance.ShouldBe(-50m);
    }
}
