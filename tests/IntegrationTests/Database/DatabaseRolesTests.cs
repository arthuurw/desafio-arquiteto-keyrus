using IntegrationTests.Infrastructure;

namespace IntegrationTests.Database;

[Collection(MigratedDatabaseDefinition.Name)]
public sealed class DatabaseRolesTests(MigratedDatabase fixture)
{
    private const string InsufficientPrivilege = "42501";

    private readonly PostgresDatabase _database = fixture.Database;

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "O nome carrega a versão 18.6; sem o sublinhado ela fica ilegível.")]
    public async Task ServerVersionIs18_6()
    {
        var version = await _database.ScalarAsync<string>("postgres", "SHOW server_version");

        version.ShouldNotBeNull();
        version.ShouldStartWith("18.6");
    }

    [Theory]
    [InlineData("SELECT * FROM {0}")]
    [InlineData("INSERT INTO {0} VALUES (2, 'b')")]
    [InlineData("UPDATE {0} SET value = 'c'")]
    [InlineData("DELETE FROM {0}")]
    public async Task LedgerAppIsDeniedOnCustomersSchema(string command)
    {
        var table = await _database.CreateMigratorTableAsync("customers");

        var sqlState = await _database.SqlStateOfAsync("ledger_app", string.Format(System.Globalization.CultureInfo.InvariantCulture, command, table));

        sqlState.ShouldBe(InsufficientPrivilege);
    }

    [Theory]
    [InlineData("SELECT * FROM {0}")]
    [InlineData("INSERT INTO {0} VALUES (2, 'b')")]
    [InlineData("UPDATE {0} SET value = 'c'")]
    [InlineData("DELETE FROM {0}")]
    public async Task CustomersAppIsDeniedOnLedgerSchema(string command)
    {
        var table = await _database.CreateMigratorTableAsync("ledger");

        var sqlState = await _database.SqlStateOfAsync("customers_app", string.Format(System.Globalization.CultureInfo.InvariantCulture, command, table));

        sqlState.ShouldBe(InsufficientPrivilege);
    }

    [Theory]
    [InlineData("ledger_app", "ledger")]
    [InlineData("ledger_app", "public")]
    [InlineData("customers_app", "customers")]
    [InlineData("customers_app", "public")]
    public async Task AppRolesCannotCreateTables(string role, string schema)
    {
        var sqlState = await _database.SqlStateOfAsync(role, $"CREATE TABLE {schema}.created_by_{role}_{Guid.NewGuid():N} (id int)");

        sqlState.ShouldBe(InsufficientPrivilege);
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("customers")]
    public async Task AppRoleHasDmlOnMigratorTables(string schema)
    {
        var role = $"{schema}_app";
        var table = await _database.CreateMigratorTableAsync(schema);

        await _database.ExecuteAsync(role, $"INSERT INTO {table} VALUES (2, 'b')");
        await _database.ExecuteAsync(role, $"UPDATE {table} SET value = 'updated' WHERE id = 2");
        var value = await _database.ScalarAsync<string>(role, $"SELECT value FROM {table} WHERE id = 2");

        value.ShouldBe("updated");
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("customers")]
    public async Task AppRoleCannotDelete(string schema)
    {
        var table = await _database.CreateMigratorTableAsync(schema);

        var sqlState = await _database.SqlStateOfAsync($"{schema}_app", $"DELETE FROM {table}");
        var remaining = await _database.ScalarAsync<long>("migrator", $"SELECT count(*) FROM {table}");

        sqlState.ShouldBe(InsufficientPrivilege);
        remaining.ShouldBe(1);
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("customers")]
    public async Task AppRoleHasExactlyDmlPrivileges(string schema)
    {
        var role = $"{schema}_app";
        var table = await _database.CreateMigratorTableAsync(schema);

        var granted = await _database.ColumnAsync("postgres", $"""
            SELECT privilege
            FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privilege
            WHERE has_table_privilege('{role}', '{table}', privilege)
            ORDER BY privilege
            """);
        var truncate = await _database.SqlStateOfAsync(role, $"TRUNCATE {table}");
        var remaining = await _database.ScalarAsync<long>("migrator", $"SELECT count(*) FROM {table}");

        granted.ShouldBe(["INSERT", "SELECT", "UPDATE"]);
        truncate.ShouldBe(InsufficientPrivilege);
        remaining.ShouldBe(1);
    }

    [Fact]
    public async Task MigratorHasNoAdministrativePrivileges()
    {
        var flags = await _database.ColumnAsync("postgres",
            "SELECT concat_ws(',', rolsuper::text, rolcreaterole::text, rolcreatedb::text) FROM pg_roles WHERE rolname = 'migrator'");

        var sqlState = await _database.SqlStateOfAsync("migrator", $"CREATE TABLE public.created_by_migrator_{Guid.NewGuid():N} (id int)");

        flags.ShouldBe(["false,false,false"]);
        sqlState.ShouldBe(InsufficientPrivilege);
    }

    [Fact]
    public async Task InitScriptCreatesRolesAndSchemas()
    {
        // Banco próprio, sem Migrator: o que existe veio só do script de init.
        await using var fresh = await PostgresDatabase.StartAsync();

        var roles = await fresh.ColumnAsync("postgres",
            "SELECT rolname::text FROM pg_roles WHERE rolname IN ('migrator', 'ledger_app', 'customers_app') ORDER BY rolname");
        var schemas = await fresh.ColumnAsync("postgres",
            "SELECT nspname || ':' || pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname IN ('ledger', 'customers') ORDER BY nspname");

        roles.ShouldBe(["customers_app", "ledger_app", "migrator"]);
        schemas.ShouldBe(["customers:migrator", "ledger:migrator"]);
    }
}
