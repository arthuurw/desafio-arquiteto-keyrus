using IntegrationTests.Infrastructure;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntegrationTests.Ledger;

public sealed class OutboxBackfillTests
{
    private const string MigrationBeforeOutbox = "20261031152234_LeastPrivilegeAndBatchedPurge";

    [Fact]
    public async Task MigrationCreatesPendingEventPerExistingEntry()
    {
        await using var database = await PostgresDatabase.StartAsync();
        await MigrateLedgerToAsync(database, MigrationBeforeOutbox);
        var three = await SeedAccountAsync(database, ["100.00", "-30.25", "0.01"]);
        var two = await SeedAccountAsync(database, ["5.00", "-1.00"]);
        var rows = new LedgerRows(database);

        var migration = await MigratorProcess.RunAsync(database.ConnectionStringFor("migrator"));

        migration.ExitCode.ShouldBe(0, migration.Output);
        (await database.ScalarAsync<long>("postgres", "SELECT count(*) FROM ledger.outbox WHERE processed_at IS NULL")).ShouldBe(5);
        foreach (var (account, count) in new[] { (three, 3), (two, 2) })
        {
            var events = await database.OutboxAsync(account);
            var entries = await rows.EntriesAsync(account);
            events.Count.ShouldBe(count);
            // Ordenados por `id`: a ordem de reserva segue a de `sequence`.
            events.Select(row => row.Sequence).ShouldBe(Enumerable.Range(1, count).Select(sequence => (long)sequence));
            for (var i = 0; i < count; i++)
            {
                events[i].ProcessedAt.ShouldBeNull();
                events[i].ShouldDescribe(entries[i], account);
            }
        }
    }

    private static async Task MigrateLedgerToAsync(PostgresDatabase database, string migration)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(database.ConnectionStringFor("migrator"), LedgerDbContext.ConfigureNpgsql)
            .Options;
        await using var context = new LedgerDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(migration, TestContext.Current.CancellationToken);
    }

    // Conta com lançamentos gravados direto, como os de antes do outbox; valores negativos são débitos.
    private static async Task<Guid> SeedAccountAsync(PostgresDatabase database, string[] amounts)
    {
        var accountId = Guid.NewGuid();
        var balance = 0m;
        var values = new List<string>();
        for (var i = 0; i < amounts.Length; i++)
        {
            var amount = decimal.Parse(amounts[i], System.Globalization.CultureInfo.InvariantCulture);
            balance += amount;
            var type = amount > 0 ? "credit" : "debit";
            var category = amount > 0 ? "PIX_IN" : "PIX_OUT";
            values.Add(FormattableString.Invariant(
                $"('{Guid.NewGuid()}', '{accountId}', {i + 1}, '{type}', {Math.Abs(amount)}, {balance}, now() + interval '{i} seconds', '{category}', 'BACKFILL-{Guid.NewGuid():N}', 'payments-hub', NULL)"));
        }

        await database.ExecuteAsync("postgres", FormattableString.Invariant($"""
            INSERT INTO ledger.accounts (id, customer_id, balance, credit_limit, version, last_recorded_at)
            VALUES ('{accountId}', '{Guid.NewGuid()}', {balance}, 0, {amounts.Length}, now() + interval '{amounts.Length - 1} seconds');
            INSERT INTO ledger.entries (id, account_id, sequence, type, amount, balance_after, recorded_at, category, external_reference, client_id, reversed_entry_id)
            VALUES {string.Join(", ", values)};
            """));
        return accountId;
    }
}
