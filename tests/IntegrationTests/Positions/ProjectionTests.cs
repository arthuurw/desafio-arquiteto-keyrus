using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;

namespace IntegrationTests.Positions;

// O Worker real projeta o outbox no MongoDB real (ADR-0056).
[Collection(PositionsProjectionDefinition.Name)]
public sealed class ProjectionTests(PositionsEnvironment environment)
{
    private const string Batch = "Outbox relay claimed";

    [Fact]
    public async Task WorkerProjectsPendingEvents()
    {
        var account = await environment.OpenAccountAsync();
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
        var account = await environment.OpenAccountAsync();
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
        var account = await environment.OpenAccountAsync();
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
        var account = await environment.OpenAccountAsync();
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
        var account = await environment.OpenAccountAsync();
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

        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "3.00");

        using var worker = environment.StartWorker();
        await environment.WaitProjectedAsync(worker, account.Id, 1);
        await environment.WaitProcessedAsync(worker, account.Id);

        (await environment.Mongo.PositionAsync(account.Id)).ShouldNotBeNull()["balance"].AsDecimal.ShouldBe(3.00m);
        worker.Output.ShouldContain("claimed 100 events: 0 applied, 0 already applied, 0 deferred, 100 malformed");
    }

    // Outro lançamento na mesma `(accountId, sequence)`, com outro `_id`, faz o índice único recusar o evento verdadeiro.
    [Fact]
    public async Task RejectedEventStaysPendingAndOthersApply()
    {
        var rejected = await environment.OpenAccountAsync();
        var other = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(rejected.Id, "5.00");
        await environment.Client.CreditOkAsync(other.Id, "7.00");
        var rejectedEvent = (await environment.Database.OutboxAsync(rejected.Id)).ShouldHaveSingleItem();
        await environment.Mongo.Entries.InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = MongoReadModel.Uuid(Guid.NewGuid()),
                ["accountId"] = MongoReadModel.Uuid(rejected.Id),
                ["sequence"] = new BsonInt64(1),
            },
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            using (var worker = environment.StartWorker())
            {
                await environment.WaitProjectedAsync(worker, other.Id, 1);
                await environment.WaitProcessedAsync(worker, other.Id);
                await worker.WaitForOutputAsync(
                    output => output.Contains(
                        $"Outbox event {rejectedEvent.Id} for account {rejected.Id} sequence 1 was rejected by the read model and stays pending",
                        StringComparison.Ordinal),
                    TimeSpan.FromSeconds(60));
                worker.Output.ShouldContain("fail: Ledger.Infrastructure.Outbox.OutboxRelay");
                // O resumo do lote sai depois do commit, então pode chegar depois da marca de processado.
                await worker.WaitForOutputAsync(RejectedBatchLogged, TimeSpan.FromSeconds(60));
            }

            (await environment.Database.OutboxAsync(rejected.Id)).ShouldHaveSingleItem().ProcessedAt.ShouldBeNull();
            (await environment.Mongo.PositionAsync(rejected.Id)).ShouldBeNull();
            (await environment.Mongo.PositionAsync(other.Id)).ShouldNotBeNull()["balance"].AsDecimal.ShouldBe(7.00m);
        }
        finally
        {
            // Pendente para sempre, o evento entraria nos lotes dos outros testes da coleção.
            await environment.Database.ExecuteAsync("postgres", $"UPDATE ledger.outbox SET processed_at = now() WHERE id = {rejectedEvent.Id}");
        }
    }

    [Fact]
    public async Task StuckEventsAreReadOncePerSweep()
    {
        var stuck = await SeedRejectedEventsAsync(99);
        var account = await environment.OpenAccountAsync();
        for (var i = 0; i < 150; i++)
        {
            await environment.Client.CreditOkAsync(account.Id, "1.00");
        }

        try
        {
            // Com 1 min entre varreduras, os 150 só chegam antes dele se a primeira varredura os alcançar.
            using var worker = environment.StartWorker(pollInterval: "00:01:00");
            await Eventually.WaitUntilAsync(
                async () =>
                {
                    worker.HasExited.ShouldBeFalse(worker.Output);
                    var position = await environment.Mongo.PositionAsync(account.Id);
                    return position is not null && position["version"].AsInt64 == 150 && BatchTotals(worker.Output).Applied >= 150;
                },
                TimeSpan.FromSeconds(50),
                () => $"150 credits not projected in the first sweep\n{worker.Output}",
                TimeSpan.FromMilliseconds(100));

            // Cada preso entra em um lote só da varredura; reler do mesmo cursor somaria 99 a cada lote.
            BatchTotals(worker.Output).Rejected.ShouldBe(99, worker.Output);
            (await environment.Database.OutboxAsync(account.Id)).ShouldAllBe(row => row.ProcessedAt != null);
        }
        finally
        {
            await MarkProcessedAsync(stuck);
        }
    }

    [Fact]
    public async Task StuckEventIsLoggedOncePerProcess()
    {
        var rejected = await SeedRejectedEventsAsync(1);
        var malformed = await environment.Database.SeedEventAsync(processedAgo: null);
        var rejectedId = await environment.Database.ScalarAsync<long>(
            "postgres", $"SELECT id FROM ledger.outbox WHERE account_id = '{rejected[0]}'");

        try
        {
            string output;
            using (var worker = environment.StartWorker(pollInterval: "00:00:00.100"))
            {
                // Cada varredura relê os dois presos; 15 lotes cobrem pelo menos 5 varreduras.
                await worker.WaitForOutputAsync(output => BatchLines(output) >= 15, TimeSpan.FromSeconds(60));
                output = worker.Output;
            }

            // Uma linha de cada `id`, e ela é `Error` (`fail:` no console do Worker).
            WorkerProcess.Occurrences(output, $"Outbox event {rejectedId} for account {rejected[0]} sequence 1 was rejected").ShouldBe(1, output);
            WorkerProcess.Occurrences(output, $"Outbox event {malformed} of type Seeded.v1 is not a valid").ShouldBe(1, output);
            ErrorLines(output, $"Outbox event {rejectedId} for account {rejected[0]} sequence 1 was rejected").ShouldBe(1, output);
            ErrorLines(output, $"Outbox event {malformed} of type Seeded.v1 is not a valid").ShouldBe(1, output);
            (await environment.Database.OutboxCountAsync($"id IN ({rejectedId}, {malformed}) AND processed_at IS NULL")).ShouldBe(2);
        }
        finally
        {
            await MarkProcessedAsync(rejected);
            await environment.Database.ExecuteAsync("postgres", $"UPDATE ledger.outbox SET processed_at = now() WHERE id = {malformed}");
        }
    }

    // Eventos `EntryRecorded.v1` válidos, cada um de uma conta, recusados pelo MongoDB: outro lançamento já ocupa a
    // sequência 1 da conta com outro `_id`.
    private async Task<Guid[]> SeedRejectedEventsAsync(int count)
    {
        var accounts = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var ids = string.Join(", ", accounts.Select(a => $"'{a}'::uuid"));
        await environment.Database.ExecuteAsync("postgres", $"""
            INSERT INTO ledger.outbox (account_id, sequence, event_type, payload)
            SELECT a, 1, 'EntryRecorded.v1', jsonb_build_object(
                'entryId', gen_random_uuid(), 'accountId', a, 'sequence', 1, 'type', 'credit', 'amount', 1.00,
                'balanceAfter', 1.00, 'category', 'DEPOSIT', 'externalReference', 'stuck-' || a, 'reversedEntryId', NULL,
                'recordedAt', now())
            FROM unnest(ARRAY[{ids}]) AS a
            ORDER BY a
            """);
        await environment.Mongo.Entries.InsertManyAsync(
            accounts.Select(a => new BsonDocument
            {
                ["_id"] = MongoReadModel.Uuid(Guid.NewGuid()),
                ["accountId"] = MongoReadModel.Uuid(a),
                ["sequence"] = new BsonInt64(1),
            }),
            cancellationToken: TestContext.Current.CancellationToken);
        return accounts;
    }

    // Pendentes para sempre, os presos entrariam nos lotes dos outros testes da coleção.
    private async Task MarkProcessedAsync(Guid[] accounts) =>
        await environment.Database.ExecuteAsync("postgres",
            $"UPDATE ledger.outbox SET processed_at = now() WHERE account_id IN ({string.Join(", ", accounts.Select(a => $"'{a}'"))})");

    // Soma de `applied` e de `rejected` nas linhas de lote do Worker.
    private static (int Applied, int Rejected) BatchTotals(string output)
    {
        var lines = System.Text.RegularExpressions.Regex.Matches(
            output, @"claimed [0-9]+ events: ([0-9]+) applied, [0-9]+ already applied, [0-9]+ deferred, [0-9]+ malformed, ([0-9]+) rejected");
        return (lines.Sum(line => int.Parse(line.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)),
            lines.Sum(line => int.Parse(line.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static int BatchLines(string output) => WorkerProcess.Occurrences(output, Batch);

    // Mensagens do relay logadas como `Error`: o console escreve `fail: <categoria>[id]` e a mensagem na linha seguinte.
    private static int ErrorLines(string output, string message) =>
        System.Text.RegularExpressions.Regex.Count(
            output, @"fail: Ledger\.Infrastructure\.Outbox\.OutboxRelay\[[0-9]+\]\s+" + System.Text.RegularExpressions.Regex.Escape(message));

    // Um lote com evento adiado: "N deferred" com N maior que zero.
    private static bool DeferredBatchLogged(string output) =>
        System.Text.RegularExpressions.Regex.IsMatch(output, "[1-9][0-9]* deferred");

    // Um lote que aplicou eventos e recusou exatamente um.
    private static bool RejectedBatchLogged(string output) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            output, @"\b[1-9][0-9]* applied, [0-9]+ already applied, [0-9]+ deferred, [0-9]+ malformed, 1 rejected\b");

    private async Task ResetProcessedAtAsync(Guid accountId, long sequence) =>
        await environment.Database.ExecuteAsync("postgres",
            $"UPDATE ledger.outbox SET processed_at = NULL WHERE account_id = '{accountId}' AND sequence = {sequence}");
}
