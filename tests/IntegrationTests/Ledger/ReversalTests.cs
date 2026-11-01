namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class ReversalTests(LedgerApi api)
{
    [Fact]
    public async Task ReversalCreatesOppositeEntry()
    {
        var account = await api.OpenAccountAsync();
        var creditId = (await api.Client.CreditOkAsync(account.Id, "100.00")).GetProperty("id").GetGuid();
        var other = await api.OpenAccountAsync();
        await api.Client.CreditOkAsync(other.Id, "50.00");
        var debitId = (await api.Client.DebitOkAsync(other, "30.00")).GetProperty("id").GetGuid();

        using var ofCredit = await api.Client.ReverseAsync(account.Id, creditId);
        using var ofDebit = await api.Client.ReverseAsync(other.Id, debitId);
        var creditReversal = await ofCredit.ShouldBeEntryAsync();
        var debitReversal = await ofDebit.ShouldBeEntryAsync();

        creditReversal.GetProperty("type").GetString().ShouldBe("debit");
        creditReversal.GetProperty("amount").GetString().ShouldBe("100.00");
        creditReversal.GetProperty("category").GetString().ShouldBe("REVERSAL");
        creditReversal.GetProperty("reversedEntryId").GetGuid().ShouldBe(creditId);
        creditReversal.GetProperty("balanceAfter").GetString().ShouldBe("0.00");
        creditReversal.GetProperty("sequence").GetInt64().ShouldBe(2);
        debitReversal.GetProperty("type").GetString().ShouldBe("credit");
        debitReversal.GetProperty("amount").GetString().ShouldBe("30.00");
        debitReversal.GetProperty("category").GetString().ShouldBe("REVERSAL");
        debitReversal.GetProperty("reversedEntryId").GetGuid().ShouldBe(debitId);
        debitReversal.GetProperty("balanceAfter").GetString().ShouldBe("50.00");
        (await api.Rows.EntriesAsync(account.Id))[^1].ClientId.ShouldBe("backoffice");
    }

    [Fact]
    public async Task CreditReversalRespectsLimit()
    {
        var account = await api.OpenAccountAsync();
        var creditId = (await api.Client.CreditOkAsync(account.Id, "100.00")).GetProperty("id").GetGuid();
        await api.Client.DebitOkAsync(account, "80.00");

        using var response = await api.Client.ReverseAsync(account.Id, creditId);

        await response.ShouldBeProblemAsync(422, "insufficient-funds");
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(2);
        (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().Balance.ShouldBe(20m);
    }

    [Fact]
    public async Task EntryIsReversedOnlyOnce()
    {
        var account = await api.OpenAccountAsync();
        var creditId = (await api.Client.CreditOkAsync(account.Id, "100.00")).GetProperty("id").GetGuid();
        using var first = await api.Client.ReverseAsync(account.Id, creditId);
        var reversalId = (await first.ShouldBeEntryAsync()).GetProperty("id").GetGuid();

        using var second = await api.Client.ReverseAsync(account.Id, creditId);
        using var ofReversal = await api.Client.ReverseAsync(account.Id, reversalId);

        await second.ShouldBeProblemAsync(409, "entry-already-reversed");
        await ofReversal.ShouldBeProblemAsync(422, "entry-is-reversal");
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task ConcurrentReversalsAcceptOne()
    {
        var account = await api.OpenAccountAsync();
        var creditId = (await api.Client.CreditOkAsync(account.Id, "100.00")).GetProperty("id").GetGuid();

        var responses = await StartGate.RunTogetherAsync(2, _ => api.Client.ReverseAsync(account.Id, creditId));
        var accepted = responses.Where(response => (int)response.StatusCode == 201).ToList();
        var refused = responses.Where(response => (int)response.StatusCode != 201).ToList();
        var reversals = (await api.Rows.EntriesAsync(account.Id)).Where(entry => entry.ReversedEntryId == creditId).ToList();

        accepted.Count.ShouldBe(1);
        refused.Count.ShouldBe(1);
        await refused[0].ShouldBeProblemAsync(409, "entry-already-reversed");
        reversals.Count.ShouldBe(1);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task UnknownEntryIsNotFound()
    {
        var accountA = await api.OpenAccountAsync();
        var accountB = await api.OpenAccountAsync();
        await api.Client.CreditOkAsync(accountA.Id, "10.00");
        var entryOfB = (await api.Client.CreditOkAsync(accountB.Id, "10.00")).GetProperty("id").GetGuid();

        using var unknown = await api.Client.ReverseAsync(accountA.Id, Guid.CreateVersion7());
        using var ofOtherAccount = await api.Client.ReverseAsync(accountA.Id, entryOfB);

        await unknown.ShouldBeProblemAsync(404, "entry-not-found");
        await ofOtherAccount.ShouldBeProblemAsync(404, "entry-not-found");
        (await api.Rows.EntriesAsync(accountA.Id)).Count.ShouldBe(1);
        (await api.Rows.EntriesAsync(accountB.Id)).Count.ShouldBe(1);
    }
}
