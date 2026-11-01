namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class OutboxTests(LedgerApi api)
{
    [Fact]
    public async Task EachConfirmedEntryWritesOneEvent()
    {
        var account = await api.Client.OpenAccountAsync();
        // Limite para o estorno do crédito caber depois do débito.
        await api.Client.SetCreditLimitOkAsync(account.Id, "100.00");

        var credit = await api.Client.CreditOkAsync(account.Id, "150.75");
        var afterCredit = await api.Database.OutboxAsync(account.Id);
        await api.Client.DebitOkAsync(account, "20.00");
        using var reversal = await api.Client.ReverseAsync(account.Id, credit.GetProperty("id").GetGuid());
        await reversal.ShouldBeEntryAsync();

        var rows = await api.Database.OutboxAsync(account.Id);
        var entries = await api.Rows.EntriesAsync(account.Id);

        afterCredit.ShouldHaveSingleItem();
        afterCredit[0].ProcessedAt.ShouldBeNull();
        afterCredit[0].Payload.GetProperty("amount").GetDecimal().ShouldBe(150.75m);
        afterCredit[0].Payload.GetProperty("balanceAfter").GetDecimal().ShouldBe(150.75m);
        afterCredit[0].Payload.GetProperty("type").GetString().ShouldBe("credit");
        rows.Count.ShouldBe(3);
        entries.Count.ShouldBe(3);
        rows.Select(row => row.Sequence).ShouldBe([1, 2, 3]);
        rows.ShouldAllBe(row => row.ProcessedAt == null);
        for (var i = 0; i < 3; i++)
        {
            rows[i].ShouldDescribe(entries[i], account.Id);
        }

        rows[1].Payload.GetProperty("type").GetString().ShouldBe("debit");
        rows[2].Payload.GetProperty("type").GetString().ShouldBe("debit");
        rows[2].Payload.GetProperty("reversedEntryId").GetGuid().ShouldBe(credit.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task RefusedOrReplayedWriteWritesNoEvent()
    {
        var account = await api.Client.OpenAccountAsync();
        var reference = LedgerClient.NewReference();
        var key = Guid.NewGuid().ToString();
        using (var first = await api.Client.CreditAsync(account.Id, "10.00", externalReference: reference, idempotencyKey: key))
        {
            await first.ShouldBeEntryAsync();
        }

        var missingId = Guid.NewGuid();
        using var missing = await api.Client.CreditAsync(missingId, "10.00");
        using var duplicate = await api.Client.CreditAsync(account.Id, "10.00", externalReference: reference);
        using var insufficient = await api.Client.DebitAsync(account, "10.01");
        using var replay = await api.Client.CreditAsync(account.Id, "10.00", externalReference: reference, idempotencyKey: key);
        HttpResponseMessage busy;
        await using (await AccountLockHolder.HoldAsync(api.Database, account.Id))
        {
            busy = await api.Client.DebitAsync(account, "1.00");
        }

        using (busy)
        {
            ((int)missing.StatusCode).ShouldBe(404);
            ((int)duplicate.StatusCode).ShouldBe(409);
            (await duplicate.BodyAsync())["type"]!.GetValue<string>().ShouldBe("duplicate-external-reference");
            ((int)insufficient.StatusCode).ShouldBe(422);
            (await insufficient.BodyAsync())["type"]!.GetValue<string>().ShouldBe("insufficient-funds");
            ((int)busy.StatusCode).ShouldBe(409);
            (await busy.BodyAsync())["type"]!.GetValue<string>().ShouldBe("concurrency-timeout");
            ((int)replay.StatusCode).ShouldBe(201);
            replay.IsReplay().ShouldBeTrue();
        }

        var rows = await api.Database.OutboxAsync(account.Id);
        rows.ShouldHaveSingleItem().Sequence.ShouldBe(1);
        (await api.Database.OutboxAsync(missingId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AccountOpeningAndLimitChangeWriteNoEvent()
    {
        var account = await api.Client.OpenAccountAsync();
        await api.Client.SetCreditLimitOkAsync(account.Id, "100.00");

        var rows = await api.Database.OutboxAsync(account.Id);

        (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().CreditLimit.ShouldBe(100m);
        rows.ShouldBeEmpty();
    }
}
