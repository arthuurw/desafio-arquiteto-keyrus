using System.Text.Json.Nodes;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class IdempotencyStorageTests(LedgerApi api)
{
    [Fact]
    public async Task NoPendingKeyAfterResponse()
    {
        var (account, _) = await api.Client.FundedAccountAsync();
        var success = Guid.NewGuid();
        var rejection = Guid.NewGuid();
        var transient = Guid.NewGuid();

        using var created = await api.Client.CreditAsync(account.Id, "10.00", idempotencyKey: success.ToString());
        var entryId = (await created.ShouldBeEntryAsync()).GetProperty("id").GetGuid();
        using var refused = await api.Client.DebitAsync(account, "1000.00", idempotencyKey: rejection.ToString());
        await refused.ShouldBeProblemAsync(422, "insufficient-funds");
        await using (await AccountLockHolder.HoldAsync(api.Database, account.Id))
        {
            using var timedOut = await api.Client.DebitAsync(account, "1.00", idempotencyKey: transient.ToString());
            await timedOut.ShouldBeProblemAsync(409, "concurrency-timeout");
        }

        var successRow = (await api.Rows.IdempotencyKeysAsync(success)).ShouldHaveSingleItem();
        var rejectionRow = (await api.Rows.IdempotencyKeysAsync(rejection)).ShouldHaveSingleItem();

        (await api.Rows.IncompleteIdempotencyKeyCountAsync()).ShouldBe(0);
        (await api.Rows.IdempotencyKeysAsync(transient)).ShouldBeEmpty();
        successRow.ClientId.ShouldBe("payments-hub");
        successRow.RequestHash.Length.ShouldBe(32);
        successRow.ResponseStatus.ShouldBe(201);
        JsonNode.Parse(successRow.ResponseBody.ShouldNotBeNull())!["id"]!.GetValue<Guid>().ShouldBe(entryId);
        rejectionRow.ResponseStatus.ShouldBe(422);
        JsonNode.Parse(rejectionRow.ResponseBody.ShouldNotBeNull())!["type"]!.GetValue<string>().ShouldBe("insufficient-funds");
    }
}
