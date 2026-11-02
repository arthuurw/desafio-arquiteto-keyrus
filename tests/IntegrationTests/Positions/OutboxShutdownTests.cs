using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;

namespace IntegrationTests.Positions;

// O Worker que recebe o encerramento no meio de um lote conclui o lote antes de sair (ADR-0021).
[Collection(PositionsProjectionDefinition.Name)]
public sealed class OutboxShutdownTests(PositionsEnvironment environment)
{
    [Fact]
    public async Task StopFinishesTheBatchInProgress()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "4.00");
        await environment.Client.CreditOkAsync(account.Id, "6.00");

        using var worker = environment.StartWorker();
        await using (await environment.Mongo.PauseAsync())
        {
            // O lote parado na escrita do MongoDB pausado, com os dois eventos da conta reservados.
            await WaitForClaimedEventsAsync(account.Id, count: 2);
            await worker.SignalStopAsync();
            await worker.WaitForOutputAsync(
                output => output.Contains("Application is shutting down", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
            worker.HasExited.ShouldBeFalse(worker.Output);
        }

        var exitCode = await worker.WaitForExitAsync(TimeSpan.FromSeconds(60));

        exitCode.ShouldBe(0, worker.Output);
        (await environment.Mongo.EntriesAsync(account.Id)).Select(document => document["sequence"].AsInt64).ShouldBe([1, 2]);
        (await environment.Mongo.PositionAsync(account.Id)).ShouldNotBeNull()["version"].AsInt64.ShouldBe(2);
        (await environment.Database.OutboxAsync(account.Id)).ShouldAllBe(row => row.ProcessedAt != null);
    }

    // Os eventos travados pela transação do lote: o `xmax` deles é a transação do `ledger_worker` vista em `pg_locks`.
    private Task WaitForClaimedEventsAsync(Guid accountId, int count) =>
        Eventually.WaitUntilAsync(
            async () => await environment.Database.ScalarAsync<long>("postgres", $"""
                SELECT count(*)
                FROM ledger.outbox o
                JOIN pg_locks l ON l.locktype = 'transactionid' AND l.transactionid = o.xmax AND l.granted
                JOIN pg_stat_activity a ON a.pid = l.pid AND a.usename = 'ledger_worker'
                WHERE o.account_id = '{accountId}' AND o.processed_at IS NULL
                """) == count,
            TimeSpan.FromSeconds(60),
            () => $"the relay never claimed the {count} events of {accountId}");
}
