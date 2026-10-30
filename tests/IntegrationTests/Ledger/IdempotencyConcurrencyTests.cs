using System.Diagnostics;
using IntegrationTests.Identity;
using Npgsql;

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

        var created = results.Where(result => result.Status == 201).ToList();
        results.Where(result => result.Status != 201).ShouldAllBe(result => result.Status == 409 && result.Type == "concurrency-timeout");
        created.Count(result => !result.Replayed).ShouldBe(1);
        created.Select(result => result.Id).Distinct().ShouldHaveSingleItem().ShouldNotBeNull();
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
            // A insere a chave e para no lock da conta; B para na chave que A ainda não confirmou.
            first = api.Client.DebitAsync(account, "10.00", externalReference: reference, idempotencyKey: key);
            await WaitForBlockedRequestsAsync(1);
            retry = api.Client.DebitAsync(account, "10.00", externalReference: reference, idempotencyKey: key);
            await WaitForBlockedRequestsAsync(2);
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
        await using (var holder = new NpgsqlConnection(api.Database.ConnectionStringFor("postgres")))
        {
            await holder.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var insert = new NpgsqlCommand(
                "INSERT INTO ledger.idempotency_keys (client_id, idempotency_key, request_hash) VALUES ('payments-hub', @key, '\\x00')",
                holder,
                transaction))
            {
                insert.Parameters.AddWithValue("key", key);
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var clock = Stopwatch.StartNew();
            blocked = await api.Client.CreditAsync(account.Id, "10.00", idempotencyKey: key.ToString());
            waited = clock.Elapsed;
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
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

    // Espera por estado observável, não por tempo: requisições da Api paradas em lock no PostgreSQL.
    private async Task WaitForBlockedRequestsAsync(int count)
    {
        var deadline = Stopwatch.StartNew();
        while (await api.Database.ScalarAsync<long>(
                   "postgres", "SELECT count(*) FROM pg_stat_activity WHERE usename = 'ledger_app' AND wait_event_type = 'Lock'") < count)
        {
            deadline.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1.5), $"{count} requests never blocked on a lock");
            await Task.Yield();
        }
    }
}
