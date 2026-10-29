namespace IntegrationTests.Infrastructure;

// Banco compartilhado pela coleção: init script aplicado e Migrator executado uma vez.
public sealed class MigratedDatabase : IAsyncLifetime
{
    internal PostgresDatabase Database { get; private set; } = null!;

    internal ProcessResult FirstMigratorRun { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Database = await PostgresDatabase.StartAsync();
        FirstMigratorRun = await MigratorProcess.RunAsync(Database.ConnectionStringFor("migrator"));
        FirstMigratorRun.ExitCode.ShouldBe(0, FirstMigratorRun.Output);
    }

    public async ValueTask DisposeAsync() => await Database.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class MigratedDatabaseDefinition : ICollectionFixture<MigratedDatabase>
{
    public const string Name = "migrated-database";
}
