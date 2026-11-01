using IntegrationTests.Infrastructure;

namespace IntegrationTests.Ledger;

// O Worker apaga os eventos processados há mais de 7 dias, no ciclo da limpeza de chaves (ADR-0035).
[Collection(MigratedDatabaseDefinition.Name)]
public sealed class OutboxPurgeTests(MigratedDatabase fixture)
{
    private const string Purged = "Outbox purge removed";
    private static readonly TimeSpan LogTimeout = TimeSpan.FromSeconds(60);

    private readonly PostgresDatabase _database = fixture.Database;

    private string WorkerConnectionString => _database.ConnectionStringFor("ledger_worker");

    [Fact]
    public async Task WorkerPurgesOldProcessedEvents()
    {
        var expired = await _database.SeedEventAsync(processedAgo: "7 days 1 minute", createdAgo: "8 days");
        var recent = await _database.SeedEventAsync(processedAgo: "6 days 23 hours", createdAgo: "8 days");
        var pending = await _database.SeedEventAsync(processedAgo: null, createdAgo: "30 days");

        using var worker = WorkerProcess.Start(WorkerConnectionString);
        await worker.WaitForOutputAsync(output => output.Contains(Purged, StringComparison.Ordinal), LogTimeout);

        (await _database.OutboxEventExistsAsync(expired)).ShouldBeFalse();
        (await _database.OutboxEventExistsAsync(recent)).ShouldBeTrue();
        (await _database.OutboxEventExistsAsync(pending)).ShouldBeTrue();
        worker.Output.ShouldMatch($@"{Purged} [1-9][0-9]* processed events");
    }

    [Fact]
    public async Task WorkerPurgesBacklogInBatches()
    {
        await _database.SeedProcessedEventsAsync(2500, "8 days");

        using var worker = WorkerProcess.Start(WorkerConnectionString);
        await worker.WaitForOutputAsync(output => output.Contains(Purged, StringComparison.Ordinal), LogTimeout);

        (await _database.OutboxCountAsync("processed_at < now() - interval '7 days'")).ShouldBe(0);
        // Lotes de 1000: 2500 vencidos pedem pelo menos 3 chamadas.
        worker.Output.ShouldMatch($@"{Purged} [0-9]+ processed events in [3-9][0-9]* batches");
    }
}
