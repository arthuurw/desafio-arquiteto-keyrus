using IntegrationTests.Infrastructure;

namespace IntegrationTests.Database;

// A trilha de auditoria só recebe linhas: a Api insere, e o DML de nenhuma role altera nem apaga (ADR-0044).
[Collection(MigratedDatabaseDefinition.Name)]
public sealed class AuditTrailTests(MigratedDatabase fixture)
{
    private const string InsufficientPrivilege = "42501";
    private const string RaisedByTrigger = "P0001";

    private static readonly string[] Schemas = ["ledger", "customers"];

    private readonly PostgresDatabase _database = fixture.Database;

    [Fact]
    public async Task AppRolesOnlyInsert()
    {
        var granted = new List<string>();
        foreach (var schema in Schemas)
        {
            foreach (var role in new[] { "ledger_app", "customers_app", "ledger_worker", "customers_worker" })
            {
                granted.AddRange((await PrivilegesAsync(role, $"{schema}.audit_events")).Select(privilege => $"{role}@{schema}:{privilege}"));
            }
        }

        var denied = new List<string>();
        var inserts = new List<string?>();
        foreach (var schema in Schemas)
        {
            var role = $"{schema}_app";
            var table = $"{schema}.audit_events";
            inserts.Add(await _database.SqlStateOfAsync(role, InsertSql(table, Guid.NewGuid())));
            foreach (var command in new[]
            {
                $"SELECT * FROM {table}",
                $"UPDATE {table} SET result = 200",
                $"DELETE FROM {table}",
                $"TRUNCATE {table}",
            })
            {
                denied.Add($"{role}: {command} -> {await _database.SqlStateOfAsync(role, command)}");
            }
        }

        granted.ShouldBe(["ledger_app@ledger:INSERT", "customers_app@customers:INSERT"], ignoreOrder: true);
        inserts.ShouldBe([null, null]);
        denied.Count.ShouldBe(8);
        denied.ShouldAllBe(result => result.EndsWith($" -> {InsufficientPrivilege}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoRoleChangesTheTrail()
    {
        var refused = new List<string>();
        var unchanged = new List<string>();
        foreach (var schema in Schemas)
        {
            var table = $"{schema}.audit_events";
            var id = Guid.NewGuid();
            await _database.ExecuteAsync("postgres", InsertSql(table, id));
            var before = await RowAsync(table, id);

            foreach (var role in new[] { "migrator", "postgres" })
            {
                foreach (var command in new[]
                {
                    $"UPDATE {table} SET result = 500, client_id = 'other', resource_id = NULL, occurred_at = now() - interval '1 year' WHERE id = '{id}'",
                    $"DELETE FROM {table} WHERE id = '{id}'",
                    $"TRUNCATE {table}",
                })
                {
                    refused.Add($"{role}: {command.Split(' ')[0]} {table} -> {await _database.SqlStateOfAsync(role, command)}");
                }
            }

            var after = await RowAsync(table, id);
            unchanged.Add(after == before ? $"{table} unchanged" : $"{table} changed: {before} -> {after}");
            before.ShouldNotBeNull().ShouldContain("backoffice");
        }

        refused.Count.ShouldBe(12);
        refused.ShouldAllBe(result => result.EndsWith($" -> {RaisedByTrigger}", StringComparison.Ordinal));
        unchanged.ShouldBe(["ledger.audit_events unchanged", "customers.audit_events unchanged"]);
    }

    // A dona ainda desliga o trigger por DDL; o comando fica no log do servidor, com quem o executou (ADR-0044).
    [Fact]
    public async Task OwnerDdlLeavesATrace()
    {
        var marker = $"ddl_trace_{Guid.NewGuid():N}";
        var statements = Schemas.Select(schema =>
            $"ALTER TABLE {schema}.audit_events DISABLE TRIGGER audit_events_reject_row_change /* {marker} */").ToList();

        foreach (var statement in statements)
        {
            await _database.ExecuteAsync("migrator", $"BEGIN; {statement}; ROLLBACK;");
        }

        await Eventually.WaitUntilAsync(
            async () => (await _database.ServerLogAsync()).Contains(marker, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10),
            () => "the owner DDL never reached the server log",
            TimeSpan.FromMilliseconds(100));
        var lines = (await _database.ServerLogAsync()).Split('\n').Where(line => line.Contains(marker, StringComparison.Ordinal)).ToList();
        lines.Count.ShouldBe(2);
        foreach (var statement in statements)
        {
            var line = lines.Where(item => item.Contains(statement, StringComparison.Ordinal)).ShouldHaveSingleItem(statement);
            line.ShouldContain("migrator@ledger_db");
            line.ShouldContain(" LOG:  ");
        }
    }

    private static string InsertSql(string table, Guid id) =>
        $"""
        INSERT INTO {table} (id, client_id, caller_customer_id, operation, resource_type, resource_id, result, trace_id)
        VALUES ('{id}', 'backoffice', NULL, 'open-account', 'account', '{Guid.NewGuid()}', 201, '00-{Guid.NewGuid():N}-0000000000000001-01')
        """;

    // Todas as colunas da linha, como texto: `id` e `occurred_at` inclusive.
    private async Task<string?> RowAsync(string table, Guid id) =>
        (await _database.ColumnAsync("postgres", $"SELECT row_to_json(a)::text FROM {table} AS a WHERE id = '{id}'")).SingleOrDefault();

    private Task<List<string>> PrivilegesAsync(string role, string table) =>
        _database.ColumnAsync("postgres", $"""
            SELECT privilege
            FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privilege
            WHERE has_table_privilege('{role}', '{table}', privilege)
            ORDER BY privilege
            """);
}
