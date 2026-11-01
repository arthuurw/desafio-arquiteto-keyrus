using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;

namespace IntegrationTests.Positions;

// O Worker real projeta o outbox no MongoDB real (ADR-0035).
[Collection(PositionsProjectionDefinition.Name)]
public sealed class ProjectionTests(PositionsEnvironment environment)
{
    private const string Batch = "Outbox relay claimed";

    [Fact]
    public async Task WorkerProjectsPendingEvents()
    {
        var account = await environment.Client.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "100.00");
        await environment.Client.DebitOkAsync(account, "30.25");
        await environment.Client.CreditOkAsync(account.Id, "0.01");

        using (var worker = environment.StartWorker())
        {
            await environment.WaitProjectedAsync(worker, account.Id, 3);
            await environment.WaitProcessedAsync(worker, account.Id);
        }

        var position = (await environment.Mongo.PositionAsync(account.Id)).ShouldNotBeNull();
        var documents = await environment.Mongo.EntriesAsync(account.Id);
        var entries = await environment.Rows.EntriesAsync(account.Id);

        position["version"].AsInt64.ShouldBe(3);
        position["balance"].AsDecimal.ShouldBe(69.76m);
        documents.Count.ShouldBe(3);
        for (var i = 0; i < 3; i++)
        {
            documents[i].ShouldDescribe(entries[i], account.Id);
        }

        (await environment.Database.OutboxAsync(account.Id)).ShouldAllBe(row => row.ProcessedAt != null);
    }

    [Fact]
    public async Task ReappliedEventChangesNothing()
    {
        var account = await environment.Client.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "10.00");
        await environment.Client.CreditOkAsync(account.Id, "20.00");
        await environment.Client.CreditOkAsync(account.Id, "30.00");
        await environment.ProjectAsync(account.Id);
        var before = await environment.Mongo.EntriesAsync(account.Id);

        // Queda entre aplicar e marcar: o terceiro volta a ser reservado.
        await ResetProcessedAtAsync(account.Id, 3);
        using (var worker = environment.StartWorker())
        {
            await environment.WaitProcessedAsync(worker, account.Id);
        }

        var afterRedelivery = await environment.Mongo.EntriesAsync(account.Id);
        var positionAfterRedelivery = (await environment.Mongo.PositionAsync(account.Id)).ShouldNotBeNull();

        // Queda entre gravar o lançamento e avançar a versão: documento do 3 gravado, posição ainda no 2.
        await environment.Mongo.Positions.UpdateOneAsync(
            new BsonDocument("_id", MongoReadModel.Uuid(account.Id)),
            new BsonDocument("$set", new BsonDocument { ["version"] = 2L, ["balance"] = new BsonDecimal128(30.00m) }),
            cancellationToken: TestContext.Current.CancellationToken);
        await ResetProcessedAtAsync(account.Id, 3);
        using (var worker = environment.StartWorker())
        {
            await environment.WaitProjectedAsync(worker, account.Id, 3);
            await environment.WaitProcessedAsync(worker, account.Id);
        }

        var afterPartial = await environment.Mongo.EntriesAsync(account.Id);
        var position = (await environment.Mongo.PositionAsync(account.Id)).ShouldNotBeNull();

        afterRedelivery.ShouldBe(before);
        positionAfterRedelivery["version"].AsInt64.ShouldBe(3);
        positionAfterRedelivery["balance"].AsDecimal.ShouldBe(60.00m);
        afterPartial.ShouldBe(before);
        afterPartial.Count.ShouldBe(3);
        position["version"].AsInt64.ShouldBe(3);
        position["balance"].AsDecimal.ShouldBe(60.00m);
    }

    [Fact]
    public async Task LaterEventWaitsForPrevious()
    {
        var account = await environment.Client.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "10.00");
        await environment.Client.CreditOkAsync(account.Id, "5.00");
        var events = await environment.Database.OutboxAsync(account.Id);

        await using (var superuser = new NpgsqlConnection(environment.Database.ConnectionStringFor("postgres")))
        {
            await superuser.OpenAsync(TestContext.Current.CancellationToken);
            await using var held = await superuser.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var command = new NpgsqlCommand($"SELECT id FROM ledger.outbox WHERE id = {events[0].Id} FOR UPDATE", superuser, held))
            {
                (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(events[0].Id);
            }

            using var worker = environment.StartWorker();
            // O lote com o evento 2 adiado: reservado, entregue e devolvido como "ainda não".
            await worker.WaitForOutputAsync(output => output.Contains(Batch, StringComparison.Ordinal) && DeferredBatchLogged(output), TimeSpan.FromSeconds(60));

            var positionWhileHeld = await environment.Mongo.PositionAsync(account.Id);
            var documentsWhileHeld = await environment.Mongo.EntriesAsync(account.Id);
            var eventsWhileHeld = await environment.Database.OutboxAsync(account.Id);

            await held.RollbackAsync(TestContext.Current.CancellationToken);
            await environment.WaitProjectedAsync(worker, account.Id, 2);
            await environment.WaitProcessedAsync(worker, account.Id);

            positionWhileHeld.ShouldBeNull();
            documentsWhileHeld.ShouldBeEmpty();
            eventsWhileHeld.ShouldAllBe(row => row.ProcessedAt == null);
        }

        (await environment.Mongo.EntriesAsync(account.Id)).Select(document => document["sequence"].AsInt64).ShouldBe([1, 2]);
        (await environment.Mongo.PositionAsync(account.Id)).ShouldNotBeNull()["balance"].AsDecimal.ShouldBe(15.00m);
    }

    [Fact]
    public async Task WorkerWaitsForMongoAndResumes()
    {
        var account = await environment.Client.OpenAccountAsync();
        using var worker = environment.StartWorker();
        using var health = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{worker.Port}") };
        await worker.WaitForOutputAsync(output => output.Contains("Now listening on", StringComparison.Ordinal), TimeSpan.FromSeconds(60));

        int liveStatus;
        List<OutboxRow> eventsWhilePaused;
        await using (await environment.Mongo.PauseAsync())
        {
            await environment.Client.CreditOkAsync(account.Id, "1.00");
            await environment.Client.CreditOkAsync(account.Id, "2.00");
            await environment.Client.CreditOkAsync(account.Id, "3.00");
            await worker.WaitForOutputAsync(output => output.Contains("Outbox relay failed", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
            using (var live = await health.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken))
            {
                liveStatus = (int)live.StatusCode;
            }

            eventsWhilePaused = await environment.Database.OutboxAsync(account.Id);
        }

        await environment.WaitProjectedAsync(worker, account.Id, 3);

        liveStatus.ShouldBe(200);
        eventsWhilePaused.Count.ShouldBe(3);
        eventsWhilePaused.ShouldAllBe(row => row.ProcessedAt == null);
        worker.Output.ShouldContain("fail: Ledger.Infrastructure.Outbox.OutboxRelay");
        worker.HasExited.ShouldBeFalse();
    }

    [Fact]
    public async Task MalformedEventDoesNotBlockOthers()
    {
        var malformed = await environment.Database.SeedEventAsync(processedAgo: null);
        var account = await environment.Client.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "7.00");

        using (var worker = environment.StartWorker())
        {
            await environment.WaitProjectedAsync(worker, account.Id, 1);
            await environment.WaitProcessedAsync(worker, account.Id);
            worker.Output.ShouldContain($"Outbox event {malformed} of type Seeded.v1 is not a valid EntryRecorded.v1 and stays pending");
        }

        (await environment.Database.OutboxCountAsync($"id = {malformed} AND processed_at IS NULL")).ShouldBe(1);
    }

    [Fact]
    public async Task FullBatchOfStuckEventsDoesNotBlockOthers()
    {
        // Mais eventos que nunca aplicam do que cabem num lote, todos antes do crédito.
        for (var i = 0; i < 150; i++)
        {
            await environment.Database.SeedEventAsync(processedAgo: null);
        }

        var account = await environment.Client.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "3.00");

        using var worker = environment.StartWorker();
        await environment.WaitProjectedAsync(worker, account.Id, 1);
        await environment.WaitProcessedAsync(worker, account.Id);

        (await environment.Mongo.PositionAsync(account.Id)).ShouldNotBeNull()["balance"].AsDecimal.ShouldBe(3.00m);
        worker.Output.ShouldContain("claimed 100 events: 0 applied, 0 already applied, 0 deferred, 100 malformed");
    }

    // Um lote com evento adiado: "N deferred" com N maior que zero.
    private static bool DeferredBatchLogged(string output) =>
        System.Text.RegularExpressions.Regex.IsMatch(output, "[1-9][0-9]* deferred");

    private async Task ResetProcessedAtAsync(Guid accountId, long sequence) =>
        await environment.Database.ExecuteAsync("postgres",
            $"UPDATE ledger.outbox SET processed_at = NULL WHERE account_id = '{accountId}' AND sequence = {sequence}");
}
