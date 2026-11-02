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

        // O código 1 sai de qualquer exceção: o log diz que foi a conexão, e não uma migração.
        result.ExitCode.ShouldBe(1, result.Output);
        result.Output.ShouldContain("Migration failed");
        result.Output.ShouldContain("Failed to connect to 127.0.0.1:1");
        result.Output.ShouldNotContain("42501");
    }

    [Fact]
    public async Task ExitsWithOneWhenMigrationCannotBeApplied()
    {
        // Banco próprio e alcançável; o histórico de Customers pertence ao superusuário, e o migrator não o lê.
        await using var database = await PostgresDatabase.StartAsync();
        await database.ExecuteAsync("postgres",
            "CREATE TABLE customers.\"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32) NOT NULL)");

        var result = await MigratorProcess.RunAsync(database.ConnectionStringFor("migrator"));

        // O código 1 sai de qualquer exceção: o log traz o erro do PostgreSQL na tabela de histórico, com o banco no ar.
        result.ExitCode.ShouldBe(1, result.Output);
        result.Output.ShouldContain("Migration failed");
        result.Output.ShouldContain("42501: permission denied for table __EFMigrationsHistory");
        result.Output.ShouldNotContain("Failed to connect");
    }

    [Fact]
    public async Task SecondRunAppliesNothingAndExitsZero()
    {
        var before = await _database.ScalarAsync<long>("postgres", HistoryCount);

        var result = await MigratorProcess.RunAsync(_database.ConnectionStringFor("migrator"));
        var after = await _database.ScalarAsync<long>("postgres", HistoryCount);

        result.ExitCode.ShouldBe(0, result.Output);
        before.ShouldBe(10);
        after.ShouldBe(before);
    }

    [Fact]
    public async Task EachSchemaRecordsItsMigrations()
    {
        var ledger = await _database.ColumnAsync("postgres", "SELECT \"MigrationId\" FROM ledger.\"__EFMigrationsHistory\" ORDER BY \"MigrationId\"");
        var customers = await _database.ColumnAsync("postgres", "SELECT \"MigrationId\" FROM customers.\"__EFMigrationsHistory\"");
        var publicHistory = await _database.ScalarAsync<string>("postgres", "SELECT to_regclass('public.\"__EFMigrationsHistory\"')::text");

        ledger.ShouldBe(
            ["20261029212322_Initial", "20261030184453_AccountsAndEntries", "20261030231853_IdempotencyKeys", "20261031152234_LeastPrivilegeAndBatchedPurge", "20261031233000_Outbox", "20261101191654_AuditEvents"]);
        customers.ShouldBe(["20261029212512_Initial", "20261031152502_AppRoleWithoutMigrationHistory", "20261101143504_CustomersAndIdempotencyKeys", "20261101191701_AuditEvents"]);
        publicHistory.ShouldBeNull();
    }
}
