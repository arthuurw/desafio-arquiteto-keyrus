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

    // Nos dois schemas, UPDATE só por coluna, concedido na migração de cada tabela (ADR-0038).
    [Theory]
    [InlineData("ledger", new[] { "INSERT", "SELECT" })]
    [InlineData("customers", new[] { "INSERT", "SELECT" })]
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
            "SELECT rolname::text FROM pg_roles WHERE rolname IN ('migrator', 'ledger_app', 'ledger_worker', 'customers_app', 'customers_worker') ORDER BY rolname");
        var schemas = await fresh.ColumnAsync("postgres",
            "SELECT nspname || ':' || pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname IN ('ledger', 'customers') ORDER BY nspname");

        roles.ShouldBe(["customers_app", "customers_worker", "ledger_app", "ledger_worker", "migrator"]);
        schemas.ShouldBe(["customers:migrator", "ledger:migrator"]);
    }

    // O Worker não lê nem escreve tabela nenhuma: tudo o que ele faz passa pelas funções do migrator (ADR-0035).
    [Fact]
    public async Task WorkerRoleExecutesOnlyItsFunctions()
    {
        var tablePrivileges = await _database.ColumnAsync("postgres", """
            SELECT c.relname || ':' || privilege
            FROM pg_class AS c
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            CROSS JOIN unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privilege
            WHERE n.nspname = 'ledger' AND c.relkind = 'r' AND has_table_privilege('ledger_worker', c.oid, privilege)
            ORDER BY 1
            """);
        var columnUpdates = await _database.ColumnAsync("postgres", """
            SELECT c.relname || '.' || a.attname
            FROM pg_attribute AS a
            JOIN pg_class AS c ON c.oid = a.attrelid
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = 'ledger' AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
              AND has_column_privilege('ledger_worker', c.oid, a.attnum, 'SELECT, INSERT, UPDATE')
            """);
        var functions = await _database.ColumnAsync("postgres", """
            SELECT p.proname::text
            FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            WHERE n.nspname = 'ledger' AND has_function_privilege('ledger_worker', p.oid, 'EXECUTE')
            ORDER BY 1
            """);
        var tables = await _database.ColumnAsync("postgres",
            // As tabelas `probe_*` são criadas pelos outros testes desta coleção.
            "SELECT relname::text FROM pg_class WHERE relnamespace = 'ledger'::regnamespace AND relkind = 'r' AND relname NOT LIKE 'probe\\_%' ORDER BY 1");
        var outboxSelect = await _database.SqlStateOfAsync("ledger_worker", "SELECT * FROM ledger.outbox");

        tables.ShouldBe(["__EFMigrationsHistory", "accounts", "entries", "idempotency_keys", "outbox"]);
        tablePrivileges.ShouldBeEmpty();
        columnUpdates.ShouldBeEmpty();
        functions.ShouldBe(["claim_outbox_batch", "mark_outbox_processed", "purge_expired_idempotency_keys", "purge_processed_outbox"]);
        outboxSelect.ShouldBe(InsufficientPrivilege);
    }

    // A Api grava o evento e nada mais: ler, marcar e apagar são do Worker (ADR-0035).
    [Fact]
    public async Task AppRoleOnlyInsertsIntoOutbox()
    {
        var granted = await _database.ColumnAsync("postgres", """
            SELECT privilege
            FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privilege
            WHERE has_table_privilege('ledger_app', 'ledger.outbox', privilege)
            ORDER BY privilege
            """);
        var denied = new List<string>();
        foreach (var command in new[]
        {
            "SELECT * FROM ledger.outbox",
            "UPDATE ledger.outbox SET processed_at = now()",
            "DELETE FROM ledger.outbox",
            "TRUNCATE ledger.outbox",
        })
        {
            denied.Add($"{command} -> {await _database.SqlStateOfAsync("ledger_app", command)}");
        }

        var insert = await _database.SqlStateOfAsync("ledger_app",
            $"INSERT INTO ledger.outbox (account_id, sequence, event_type, payload) VALUES ('{Guid.NewGuid()}', 1, 'EntryRecorded.v1', '{{}}')");

        granted.ShouldBe(["INSERT"]);
        denied.ShouldAllBe(result => result.EndsWith($" -> {InsufficientPrivilege}", StringComparison.Ordinal));
        denied.Count.ShouldBe(4);
        insert.ShouldBeNull();
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

    [Theory]
    [InlineData("ledger")]
    [InlineData("customers")]
    public async Task NewTablesGrantNoTableWideUpdate(string schema)
    {
        var table = await _database.CreateMigratorTableAsync(schema);

        var sqlState = await _database.SqlStateOfAsync($"{schema}_app", $"UPDATE {table} SET value = 'c'");

        sqlState.ShouldBe(InsufficientPrivilege);
    }

    // O cliente não muda depois do cadastro, e da chave só a resposta muda (ADR-0038).
    [Fact]
    public async Task CustomersAppHasExactlyItsPrivileges()
    {
        var granted = await _database.ColumnAsync("postgres", """
            SELECT c.relname || ':' || privilege
            FROM pg_class AS c
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            CROSS JOIN unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privilege
            WHERE n.nspname = 'customers' AND c.relname IN ('customers', 'idempotency_keys')
              AND has_table_privilege('customers_app', c.oid, privilege)
            ORDER BY 1
            """);
        var columnUpdates = await _database.ColumnAsync("postgres", """
            SELECT c.relname || '.' || a.attname
            FROM pg_attribute AS a
            JOIN pg_class AS c ON c.oid = a.attrelid
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = 'customers' AND c.relname IN ('customers', 'idempotency_keys') AND a.attnum > 0 AND NOT a.attisdropped
              AND has_column_privilege('customers_app', c.oid, a.attnum, 'UPDATE')
            ORDER BY 1
            """);
        string[] deniedCommands =
        [
            "UPDATE customers.customers SET id = id",
            "UPDATE customers.customers SET name = name",
            "UPDATE customers.customers SET cpf = cpf",
            "UPDATE customers.customers SET birth_date = birth_date",
            "UPDATE customers.customers SET email = email",
            "UPDATE customers.customers SET cpf_blind_index = cpf_blind_index",
            "UPDATE customers.customers SET cpf_blind_index_version = cpf_blind_index_version",
            "UPDATE customers.customers SET registered_at = registered_at",
            "DELETE FROM customers.customers",
            "UPDATE customers.idempotency_keys SET created_at = now() - interval '8 days'",
            "DELETE FROM customers.idempotency_keys",
        ];
        var denied = new List<string>();
        foreach (var command in deniedCommands)
        {
            denied.Add($"{command} -> {await _database.SqlStateOfAsync("customers_app", command)}");
        }

        var keys = await _database.SqlStateOfAsync("customers_app",
            "UPDATE customers.idempotency_keys SET response_status = response_status, response_body = response_body");

        granted.ShouldBe(["customers:INSERT", "customers:SELECT", "idempotency_keys:INSERT", "idempotency_keys:SELECT"]);
        columnUpdates.ShouldBe(["idempotency_keys.response_body", "idempotency_keys.response_status"]);
        denied.ShouldAllBe(result => result.EndsWith($" -> {InsufficientPrivilege}", StringComparison.Ordinal));
        denied.Count.ShouldBe(deniedCommands.Length);
        keys.ShouldBeNull();
    }

    // O Worker de Customers só chama a limpeza de chaves: nenhum privilégio de tabela, nada em `ledger` (ADR-0038).
    [Fact]
    public async Task CustomersWorkerExecutesOnlyThePurge()
    {
        var schemaPrivileges = await _database.ColumnAsync("postgres", """
            SELECT nspname || ':' || privilege
            FROM pg_namespace
            CROSS JOIN unnest(ARRAY['USAGE', 'CREATE']) AS privilege
            WHERE nspname IN ('ledger', 'customers') AND has_schema_privilege('customers_worker', oid, privilege)
            ORDER BY 1
            """);
        var tablePrivileges = await _database.ColumnAsync("postgres", """
            SELECT n.nspname || '.' || c.relname || ':' || privilege
            FROM pg_class AS c
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            CROSS JOIN unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privilege
            WHERE n.nspname IN ('ledger', 'customers') AND c.relkind = 'r' AND has_table_privilege('customers_worker', c.oid, privilege)
            ORDER BY 1
            """);
        var columnPrivileges = await _database.ColumnAsync("postgres", """
            SELECT c.relname || '.' || a.attname
            FROM pg_attribute AS a
            JOIN pg_class AS c ON c.oid = a.attrelid
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('ledger', 'customers') AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
              AND has_column_privilege('customers_worker', c.oid, a.attnum, 'SELECT, INSERT, UPDATE')
            """);
        var functions = await _database.ColumnAsync("postgres", """
            SELECT n.nspname || '.' || p.proname
            FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            WHERE n.nspname IN ('ledger', 'customers') AND has_function_privilege('customers_worker', p.oid, 'EXECUTE')
            ORDER BY 1
            """);
        var select = await _database.SqlStateOfAsync("customers_worker", "SELECT * FROM customers.idempotency_keys");

        schemaPrivileges.ShouldBe(["customers:USAGE"]);
        tablePrivileges.ShouldBeEmpty();
        columnPrivileges.ShouldBeEmpty();
        functions.ShouldBe(["customers.purge_expired_idempotency_keys"]);
        select.ShouldBe(InsufficientPrivilege);
    }
}
