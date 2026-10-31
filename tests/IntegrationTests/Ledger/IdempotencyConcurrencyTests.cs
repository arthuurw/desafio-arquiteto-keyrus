using System.Diagnostics;
using System.Text.Json.Nodes;
using IntegrationTests.Identity;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class IdempotencyConcurrencyTests(LedgerApi api)
{
    [Fact]
    public async Task ConcurrentRetriesRecordOneEntry()
    {
        var account = await api.Client.OpenAccountAsync();
        var key = Guid.NewGuid().ToString();
        var reference = LedgerClient.NewReference();

        var results = await StartGate.RunTogetherAsync(10, async _ =>
        {
            using var response = await api.Client.CreditAsync(account.Id, "10.00", externalReference: reference, idempotencyKey: key);
            var body = await response.JsonAsync();
            return (
                Status: (int)response.StatusCode,
                Replayed: response.IsReplay(),
                Id: body.TryGetProperty("id", out var id) ? id.GetGuid() : (Guid?)null);
        });

        // A original confirma bem antes do `lock_timeout`: as outras nove esperam por ela e repetem a resposta.
        results.ShouldAllBe(result => result.Status == 201);
        results.Select(result => result.Id).Distinct().ShouldHaveSingleItem().ShouldNotBeNull();
        results.Count(result => result.Replayed).ShouldBe(9);
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task WaitingRetryGetsCommittedResponse()
    {
        var account = await api.Client.OpenAccountAsync();
        var key = Guid.NewGuid().ToString();
        var reference = LedgerClient.NewReference();
        Task<HttpResponseMessage> first;
        Task<HttpResponseMessage> retry;

        await using (await AccountLockHolder.HoldAsync(api.Database, account.Id))
        {
            var waits = api.Database.LockWaitBudget();
            // A primeira insere a chave e para no lock da conta; a repetição para na chave ainda não confirmada.
            first = api.Client.CreditAsync(account.Id, "10.00", externalReference: reference, idempotencyKey: key);
            await waits.WaitForBlockedRequestsAsync(1);
            retry = api.Client.CreditAsync(account.Id, "10.00", externalReference: reference, idempotencyKey: key);
            await waits.WaitForBlockedRequestsAsync(2);
        }

        using var firstResponse = await first;
        using var retryResponse = await retry;

        await firstResponse.ShouldBeEntryAsync();
        await retryResponse.ShouldBeEntryAsync();
        firstResponse.IsReplay().ShouldBeFalse();
        retryResponse.IsReplay().ShouldBeTrue();
        var firstBody = await firstResponse.BodyAsync();
        var retryBody = await retryResponse.BodyAsync();
        JsonNode.DeepEquals(firstBody, retryBody).ShouldBeTrue($"{firstBody.ToJsonString()} vs {retryBody.ToJsonString()}");
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task DivergentRetryWaitingForOriginalIsRejected()
    {
        var account = await api.Client.OpenAccountAsync();
        var key = Guid.NewGuid().ToString();
        var reference = LedgerClient.NewReference();
        Task<HttpResponseMessage> original;
        Task<HttpResponseMessage> divergent;

        await using (await AccountLockHolder.HoldAsync(api.Database, account.Id))
        {
            var waits = api.Database.LockWaitBudget();
            original = api.Client.CreditAsync(account.Id, "10.00", externalReference: reference, idempotencyKey: key);
            await waits.WaitForBlockedRequestsAsync(1);
            // Só o valor muda: a espera na chave termina com o hash da original já confirmado.
            divergent = api.Client.CreditAsync(account.Id, "20.00", externalReference: reference, idempotencyKey: key);
            await waits.WaitForBlockedRequestsAsync(2);
        }

        using var originalResponse = await original;
        using var divergentResponse = await divergent;

        await originalResponse.ShouldBeEntryAsync();
        await divergentResponse.ShouldBeProblemAsync(422, "idempotency-key-reused");
        divergentResponse.IsReplay().ShouldBeFalse();
        (await api.Rows.EntriesAsync(account.Id)).ShouldHaveSingleItem().Amount.ShouldBe(10.00m);
    }

    [Fact]
    public async Task KeyPurgedDuringWaitExecutesAsNew()
    {
        var account = await api.Client.OpenAccountAsync();
        var key = Guid.NewGuid();
        Guid originalId;
        using (var original = await api.Client.CreditAsync(account.Id, "10.00", idempotencyKey: key.ToString()))
        {
            originalId = (await original.ShouldBeEntryAsync()).GetProperty("id").GetGuid();
        }

        await api.Database.ExecuteAsync(
            "postgres", $"UPDATE ledger.idempotency_keys SET created_at = now() - interval '8 days' WHERE idempotency_key = '{key}'");
        Task<HttpResponseMessage> retry;

        // A limpeza apaga a chave numa transação ainda aberta; a repetição para na chave até ela confirmar.
        await using (var purge = await HeldWrite.StartAsync(
            api.Database, $"DELETE FROM ledger.idempotency_keys WHERE idempotency_key = '{key}' AND created_at < now() - interval '7 days'"))
        {
            // Referência nova: com a mesma, a operação executada como nova pararia na referência única, com `409`.
            retry = api.Client.CreditAsync(account.Id, "10.00", idempotencyKey: key.ToString());
            await api.Database.WaitForBlockedRequestsAsync(1);
            await purge.CommitAsync();
        }

        using var retryResponse = await retry;

        var retryId = (await retryResponse.ShouldBeEntryAsync()).GetProperty("id").GetGuid();
        retryResponse.IsReplay().ShouldBeFalse();
        retryResponse.Headers.Contains(LedgerClient.ReplayedHeader).ShouldBeFalse();
        retryId.ShouldNotBe(originalId);
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task KeyWaitTimeoutDoesNotConsumeKey()
    {
        var account = await api.Client.OpenAccountAsync();
        var key = Guid.NewGuid();
        HttpResponseMessage blocked;
        TimeSpan waited;

        // Outra transação segura a chave sem confirmar: o INSERT da Api espera por ela até o `lock_timeout`.
        // Descartada sem confirmar, a chave some.
        await using (await HeldWrite.StartAsync(
            api.Database,
            $"INSERT INTO ledger.idempotency_keys (client_id, idempotency_key, request_hash) VALUES ('payments-hub', '{key}', '\\x00')"))
        {
            var clock = Stopwatch.StartNew();
            blocked = await api.Client.CreditAsync(account.Id, "10.00", idempotencyKey: key.ToString());
            waited = clock.Elapsed;
        }

        using (blocked)
        {
            await blocked.ShouldBeProblemAsync(409, "concurrency-timeout");
        }

        using var retry = await api.Client.CreditAsync(account.Id, "10.00", idempotencyKey: key.ToString());

        waited.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9));
        await retry.ShouldBeEntryAsync();
        retry.IsReplay().ShouldBeFalse();
    }

    [Fact]
    public async Task TransientFailureDoesNotConsumeKey()
    {
        var (account, _) = await api.Client.FundedAccountAsync();
        var key = Guid.NewGuid();
        List<IdempotencyKeyRow> keysAfterTimeout;
        int entriesAfterTimeout;

        await using (await AccountLockHolder.HoldAsync(api.Database, account.Id))
        {
            using var timedOut = await api.Client.DebitAsync(account, "10.00", idempotencyKey: key.ToString());
            await timedOut.ShouldBeProblemAsync(409, "concurrency-timeout");
            keysAfterTimeout = await api.Rows.IdempotencyKeysAsync(key);
            entriesAfterTimeout = (await api.Rows.EntriesAsync(account.Id)).Count;
        }

        using var retry = await api.Client.DebitAsync(account, "10.00", idempotencyKey: key.ToString());

        keysAfterTimeout.ShouldBeEmpty();
        entriesAfterTimeout.ShouldBe(1);
        await retry.ShouldBeEntryAsync();
        retry.IsReplay().ShouldBeFalse();
    }
}
