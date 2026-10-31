using System.Diagnostics;
using IntegrationTests.Identity;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class DebitConcurrencyTests(LedgerApi api)
{
    [Fact]
    public async Task ConcurrentDebitsNeverExceedLimit()
    {
        var (account, results) = await FiftyConcurrentDebitsAsync();
        var row = await api.Rows.AccountAsync(account.Id);

        results.Count(result => result.Status == 201).ShouldBe(33);
        results.Where(result => result.Status != 201).ShouldAllBe(result => result.Status == 422 && result.Type == "insufficient-funds");
        results.Count(result => result.Status == 422).ShouldBe(17);
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
                // O teto tem folga para a máquina carregada e ainda separa 2 s de uma espera de 5 s.
                waited.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9));
                waited.ShouldBeLessThan(TimeSpan.FromSeconds(4.5));
                response.Dispose();
            }
        }

        var afterTimeout = await api.Rows.AccountAsync(account.Id);
        var entriesAfterTimeout = await api.Rows.EntriesAsync(account.Id);

        // Um após o outro, numa ordem que o saldo comporta: disparadas juntas, o estorno de 100.00 poderia chegar
        // antes do crédito e ser recusado por saldo, sem defeito nenhum.
        using var limit = await api.Client.SetCreditLimitAsync(account.Id, "10.00");
        using var released = await api.Client.CreditAsync(account.Id, "1.00");
        using var debit = await api.Client.DebitAsync(account, "1.00");
        using var reversal = await api.Client.ReverseAsync(account.Id, entryId);

        afterTimeout.ShouldBe(before);
        entriesAfterTimeout.Count.ShouldBe(1);
        ((int)limit.StatusCode).ShouldBe(204, await limit.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await released.ShouldBeEntryAsync();
        await debit.ShouldBeEntryAsync();
        await reversal.ShouldBeEntryAsync();
        (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().Balance.ShouldBe(0m);
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
    private async Task<(TestAccount Account, (int Status, string? Type)[] Results)> FiftyConcurrentDebitsAsync()
    {
        var account = await api.Client.OpenAccountAsync();
        await api.Client.CreditOkAsync(account.Id, "1000.00");

        // Cada resposta devolve o status e o `type` do corpo; o teste afirma sobre o conjunto.
        var results = await StartGate.RunTogetherAsync(50, async _ =>
        {
            using var response = await api.Client.DebitAsync(account, "30.00");
            var body = await response.JsonAsync();
            return ((int)response.StatusCode, body.TryGetProperty("type", out var type) ? type.GetString() : null);
        });

        return (account, results);
    }
}
