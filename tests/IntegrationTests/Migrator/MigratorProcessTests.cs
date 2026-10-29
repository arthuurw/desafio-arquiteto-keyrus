using IntegrationTests.Infrastructure;

namespace IntegrationTests.Migrator;

[Collection(MigratedDatabaseDefinition.Name)]
public sealed class MigratorProcessTests(MigratedDatabase fixture)
{
    private const string HistoryCount =
        "SELECT (SELECT count(*) FROM ledger.\"__EFMigrationsHistory\") + (SELECT count(*) FROM customers.\"__EFMigrationsHistory\")";

    private readonly PostgresDatabase _database = fixture.Database;

    [Fact]
    public async Task ExitsWithOneWhenDatabaseIsUnreachable()
    {
        // Porta 1: nenhum PostgreSQL escuta ali.
        var result = await MigratorProcess.RunAsync("Host=127.0.0.1;Port=1;Database=ledger_db;Username=migrator;Password=migrator_dev;Timeout=5");

        result.ExitCode.ShouldBe(1, result.Output);
    }

    [Fact]
    public async Task SecondRunAppliesNothingAndExitsZero()
    {
        var before = await _database.ScalarAsync<long>("postgres", HistoryCount);

        var result = await MigratorProcess.RunAsync(_database.ConnectionStringFor("migrator"));
        var after = await _database.ScalarAsync<long>("postgres", HistoryCount);

        result.ExitCode.ShouldBe(0, result.Output);
        before.ShouldBe(2);
        after.ShouldBe(before);
    }

    [Fact]
    public async Task EachSchemaRecordsItsInitialMigration()
    {
        var ledger = await _database.ColumnAsync("postgres", "SELECT \"MigrationId\" FROM ledger.\"__EFMigrationsHistory\"");
        var customers = await _database.ColumnAsync("postgres", "SELECT \"MigrationId\" FROM customers.\"__EFMigrationsHistory\"");
        var publicHistory = await _database.ScalarAsync<string>("postgres", "SELECT to_regclass('public.\"__EFMigrationsHistory\"')::text");

        ledger.ShouldHaveSingleItem().ShouldEndWith("_Initial");
        customers.ShouldHaveSingleItem().ShouldEndWith("_Initial");
        publicHistory.ShouldBeNull();
    }
}
