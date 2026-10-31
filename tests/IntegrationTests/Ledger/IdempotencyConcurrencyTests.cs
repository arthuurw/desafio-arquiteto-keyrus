using System.Diagnostics;
using IntegrationTests.Identity;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class IdempotencyConcurrencyTests(LedgerApi api)
{
    [Fact]
    public async Task ConcurrentRetriesRecordOneEntry()
    {
        var (account, _) = await api.Client.FundedAccountAsync();
        var key = Guid.NewGuid().ToString();
        var reference = LedgerClient.NewReference();

        var results = await StartGate.RunTogetherAsync(10, async _ =>
        {
            using var response = await api.Client.DebitAsync(account, "10.00", externalReference: reference, idempotencyKey: key);
            var body = await response.JsonAsync();
            return (
                Status: (int)response.StatusCode,
                Replayed: response.IsReplay(),
                Id: body.TryGetProperty("id", out var id) ? id.GetGuid() : (Guid?)null,
                Type: body.TryGetProperty("type", out var type) ? type.GetString() : null);
        });

        // Exatamente uma execução; toda outra resposta é a repetição dela ou a desistência pela espera.
        var original = results.Where(result => result.Status == 201 && !result.Replayed).ShouldHaveSingleItem();
        original.Id.ShouldNotBeNull();
        results.ShouldAllBe(result =>
            (result.Status == 201 && result.Id == original.Id) || (result.Status == 409 && result.Type == "concurrency-timeout"));
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(2);
        (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().Balance.ShouldBe(90m);
    }

    [Fact]
    public async Task WaitingRetryGetsCommittedResponse()
    {
        var (account, _) = await api.Client.FundedAccountAsync();
        var key = Guid.NewGuid().ToString();
        var reference = LedgerClient.NewReference();
        Task<HttpResponseMessage> first;
        Task<HttpResponseMessage> retry;

        await using (await AccountLockHolder.HoldAsync(api.Database, account.Id))
        {
            // A primeira insere a chave e para no lock da conta; a repetição para na chave ainda não confirmada.
            first = api.Client.DebitAsync(account, "10.00", externalReference: reference, idempotencyKey: key);
            await api.Database.WaitForBlockedRequestsAsync(1);
            retry = api.Client.DebitAsync(account, "10.00", externalReference: reference, idempotencyKey: key);
            await api.Database.WaitForBlockedRequestsAsync(2);
        }

        using var firstResponse = await first;
        using var retryResponse = await retry;

        var firstId = (await firstResponse.ShouldBeEntryAsync()).GetProperty("id").GetGuid();
        (await retryResponse.ShouldBeEntryAsync()).GetProperty("id").GetGuid().ShouldBe(firstId);
        firstResponse.IsReplay().ShouldBeFalse();
        retryResponse.IsReplay().ShouldBeTrue();
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
        await using (await HeldInsert.StartAsync(
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
