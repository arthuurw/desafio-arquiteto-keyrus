using System.Diagnostics;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class DebitConcurrencyTests(LedgerApi api)
{
    [Fact]
    public async Task ConcurrentDebitsNeverExceedLimit()
    {
        var (account, statuses) = await FiftyConcurrentDebitsAsync();
        var row = await api.Rows.AccountAsync(account.Id);

        statuses.Count(status => status == 201).ShouldBe(33);
        statuses.Count(status => status == 422).ShouldBe(17);
        row.ShouldNotBeNull().Balance.ShouldBe(10m);
        row.Version.ShouldBe(34);
    }

    [Fact]
    public async Task ConcurrentDebitsKeepInvariants()
    {
        var (account, _) = await FiftyConcurrentDebitsAsync();

        await api.Rows.ShouldKeepInvariantsAsync(account.Id);
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(34);
    }

    [Fact]
    public async Task LockTimeoutReturnsConflict()
    {
        var account = await api.Client.OpenAccountAsync();
        var credit = await api.Client.CreditOkAsync(account.Id, "100.00");
        var entryId = credit.GetProperty("id").GetGuid();
        var before = await api.Rows.AccountAsync(account.Id);

        await using (await AccountLockHolder.HoldAsync(api.Database, account.Id))
        {
            // Uma rota por vez: cada uma é a única na fila, e a espera medida é a do próprio lock.
            foreach (var write in WriteToAllRoutes(account, entryId))
            {
                var (response, waited) = await TimedAsync(write);
                await response.ShouldBeProblemAsync(409, "concurrency-timeout");
                // Em torno do `lock_timeout` de 2 s: nem recusa imediata, nem espera até o lock ser liberado.
                waited.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9));
                waited.ShouldBeLessThan(TimeSpan.FromSeconds(3));
                response.Dispose();
            }
        }

        var afterTimeout = await api.Rows.AccountAsync(account.Id);
        var entriesAfterTimeout = await api.Rows.EntriesAsync(account.Id);

        var released = await Task.WhenAll(WriteToAllRoutes(account, entryId).Select(write => write()));

        afterTimeout.ShouldBe(before);
        entriesAfterTimeout.Count.ShouldBe(1);
        await released[0].ShouldBeEntryAsync();
        await released[1].ShouldBeEntryAsync();
        await released[2].ShouldBeEntryAsync();
        ((int)released[3].StatusCode).ShouldBe(204);
        foreach (var response in released)
        {
            response.Dispose();
        }
    }

    private Func<Task<HttpResponseMessage>>[] WriteToAllRoutes(TestAccount account, Guid entryId) =>
    [
        () => api.Client.CreditAsync(account.Id, "1.00"),
        () => api.Client.DebitAsync(account, "1.00"),
        () => api.Client.ReverseAsync(account.Id, entryId),
        () => api.Client.SetCreditLimitAsync(account.Id, "10.00"),
    ];

    private static async Task<(HttpResponseMessage Response, TimeSpan Waited)> TimedAsync(Func<Task<HttpResponseMessage>> write)
    {
        var clock = Stopwatch.StartNew();
        var response = await write();
        return (response, clock.Elapsed);
    }

    // 50 débitos de 30.00 sobre 1000.00, limite 0: a divisão não exata prova a recusa na borda.
    private async Task<(TestAccount Account, int[] Statuses)> FiftyConcurrentDebitsAsync()
    {
        var account = await api.Client.OpenAccountAsync();
        await api.Client.CreditOkAsync(account.Id, "1000.00");

        var statuses = await StartGate.RunTogetherAsync(50, async _ =>
        {
            using var response = await api.Client.DebitAsync(account, "30.00");
            if ((int)response.StatusCode == 422)
            {
                await response.ShouldBeProblemAsync(422, "insufficient-funds");
            }

            return (int)response.StatusCode;
        });

        return (account, statuses);
    }
}
