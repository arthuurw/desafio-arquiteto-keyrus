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
        var value = await _database.ScalarAsync<string>(role, $"SELECT value FROM {table} WHERE id = 2");

        value.ShouldBe("b");
    }

    [Fact]
    public async Task CustomersAppUpdatesMigratorTables()
    {
        var table = await _database.CreateMigratorTableAsync("customers");

        await _database.ExecuteAsync("customers_app", $"UPDATE {table} SET value = 'updated' WHERE id = 1");

        (await _database.ScalarAsync<string>("customers_app", $"SELECT value FROM {table} WHERE id = 1")).ShouldBe("updated");
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

    // No Ledger, UPDATE só por coluna, concedido na migração de cada tabela.
    [Theory]
    [InlineData("ledger", new[] { "INSERT", "SELECT" })]
    [InlineData("customers", new[] { "INSERT", "SELECT", "UPDATE" })]
    public async Task AppRoleHasExactlyDmlPrivileges(string schema, string[] expected)
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

        granted.ShouldBe(expected);
        truncate.ShouldBe(InsufficientPrivilege);
        remaining.ShouldBe(1);
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("customers")]
    public async Task AppRoleCannotExecuteMigratorFunctions(string schema)
    {
        var role = $"{schema}_app";
        var function = $"{schema}.probe_{Guid.NewGuid():N}";
        // SECURITY DEFINER roda com os privilégios do migrator: executável pela aplicação, contornaria o DML restrito.
        await _database.ExecuteAsync("migrator", $"CREATE FUNCTION {function}() RETURNS int LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'");

        var canExecute = await _database.ScalarAsync<bool>("postgres", $"SELECT has_function_privilege('{role}', '{function}()', 'EXECUTE')");
        var sqlState = await _database.SqlStateOfAsync(role, $"SELECT {function}()");

        canExecute.ShouldBeFalse();
        sqlState.ShouldBe(InsufficientPrivilege);
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
            "SELECT rolname::text FROM pg_roles WHERE rolname IN ('migrator', 'ledger_app', 'ledger_worker', 'customers_app') ORDER BY rolname");
        var schemas = await fresh.ColumnAsync("postgres",
            "SELECT nspname || ':' || pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname IN ('ledger', 'customers') ORDER BY nspname");

        roles.ShouldBe(["customers_app", "ledger_app", "ledger_worker", "migrator"]);
        schemas.ShouldBe(["customers:migrator", "ledger:migrator"]);
    }

    [Fact]
    public async Task WorkerRoleOnlyExecutesPurge()
    {
        // Tabela e uma coluna dela, para o UPDATE chegar à checagem de privilégio.
        (string Table, string Column)[] tables = [("ledger.accounts", "id"), ("ledger.entries", "id"), ("ledger.idempotency_keys", "client_id")];
        var denied = new List<string>();
        foreach (var (table, column) in tables)
        {
            foreach (var command in new[] { $"SELECT * FROM {table}", $"INSERT INTO {table} DEFAULT VALUES", $"UPDATE {table} SET {column} = {column}" })
            {
                var sqlState = await _database.SqlStateOfAsync("ledger_worker", command);
                denied.Add($"{command} -> {sqlState}");
            }
        }

        var purge = await _database.SqlStateOfAsync("ledger_worker", "SELECT ledger.purge_expired_idempotency_keys(1000)");
        var appPurge = await _database.SqlStateOfAsync("ledger_app", "SELECT ledger.purge_expired_idempotency_keys(1000)");

        denied.ShouldAllBe(result => result.EndsWith($" -> {InsufficientPrivilege}", StringComparison.Ordinal));
        denied.Count.ShouldBe(9);
        purge.ShouldBeNull();
        appPurge.ShouldBe(InsufficientPrivilege);
    }

    [Fact]
    public async Task AppRoleUpdatesOnlyWrittenColumns()
    {
        string[] deniedCommands =
        [
            "UPDATE ledger.accounts SET customer_id = gen_random_uuid()",
            "UPDATE ledger.accounts SET id = gen_random_uuid()",
            "UPDATE ledger.accounts SET created_at = now()",
            "UPDATE ledger.idempotency_keys SET created_at = now() - interval '8 days'",
            "UPDATE ledger.idempotency_keys SET request_hash = request_hash",
            "UPDATE ledger.idempotency_keys SET client_id = 'other'",
        ];
        var denied = new List<string>();
        foreach (var command in deniedCommands)
        {
            denied.Add($"{command} -> {await _database.SqlStateOfAsync("ledger_app", command)}");
        }

        var accounts = await _database.SqlStateOfAsync("ledger_app",
            "UPDATE ledger.accounts SET balance = balance, credit_limit = credit_limit, version = version, last_recorded_at = last_recorded_at");
        var keys = await _database.SqlStateOfAsync("ledger_app",
            "UPDATE ledger.idempotency_keys SET response_status = response_status, response_body = response_body");

        denied.ShouldAllBe(result => result.EndsWith($" -> {InsufficientPrivilege}", StringComparison.Ordinal));
        denied.Count.ShouldBe(6);
        accounts.ShouldBeNull();
        keys.ShouldBeNull();
    }

    [Theory]
    [InlineData("ledger_app", "ledger")]
    [InlineData("customers_app", "customers")]
    public async Task AppRolesCannotWriteMigrationHistory(string role, string schema)
    {
        var history = $"{schema}.\"__EFMigrationsHistory\"";

        var insert = await _database.SqlStateOfAsync(role, $"INSERT INTO {history} VALUES ('29991231000000_Future', '10.0.14')");
        var update = await _database.SqlStateOfAsync(role, $"UPDATE {history} SET \"ProductVersion\" = 'x'");
        var select = await _database.SqlStateOfAsync(role, $"SELECT * FROM {history}");

        insert.ShouldBe(InsufficientPrivilege);
        update.ShouldBe(InsufficientPrivilege);
        select.ShouldBe(InsufficientPrivilege);
    }

    [Fact]
    public async Task NewTablesGrantNoTableWideUpdate()
    {
        var table = await _database.CreateMigratorTableAsync("ledger");

        var sqlState = await _database.SqlStateOfAsync("ledger_app", $"UPDATE {table} SET value = 'c'");

        sqlState.ShouldBe(InsufficientPrivilege);
    }
}
