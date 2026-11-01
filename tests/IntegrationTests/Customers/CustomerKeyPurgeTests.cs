using IntegrationTests.Infrastructure;

namespace IntegrationTests.Customers;

[Collection(MigratedDatabaseDefinition.Name)]
public sealed class CustomerKeyPurgeTests(MigratedDatabase fixture)
{
    private const string InvalidParameterValue = "22023";
    private const string Purged = "Customers idempotency key purge removed";
    private static readonly TimeSpan LogTimeout = TimeSpan.FromSeconds(30);

    private readonly PostgresDatabase _database = fixture.Database;

    [Fact]
    public async Task WorkerPurgesExpiredCustomerKeys()
    {
        await _database.ExecuteAsync("postgres", "DELETE FROM customers.idempotency_keys WHERE created_at < now() - interval '6 days'");
        var expired = Guid.NewGuid();
        var almostExpired = Guid.NewGuid();
        await _database.ExecuteAsync("postgres", $"""
            INSERT INTO customers.idempotency_keys (client_id, idempotency_key, request_hash, response_status, response_body, created_at)
            VALUES ('purge-customers', '{expired}', '\x00', 201, '{"{}"}', now() - interval '7 days 1 minute'),
                   ('purge-customers', '{almostExpired}', '\x00', 201, '{"{}"}', now() - interval '7 days' + interval '1 minute')
            """);
        await _database.ExecuteAsync("postgres", """
            INSERT INTO customers.idempotency_keys (client_id, idempotency_key, request_hash, response_status, response_body, created_at)
            SELECT 'purge-customers-batch', gen_random_uuid(), '\x00', 201, '{}', now() - interval '8 days' FROM generate_series(1, 2500)
            """);

        using var worker = WorkerProcess.Start(new Dictionary<string, string>
        {
            ["ConnectionStrings__Ledger"] = _database.ConnectionStringFor("ledger_worker"),
            ["ConnectionStrings__Customers"] = _database.ConnectionStringFor("customers_worker"),
        });
        await worker.WaitForOutputAsync(output => output.Contains(Purged, StringComparison.Ordinal), LogTimeout);

        worker.Output.ShouldContain($"{Purged} 2501 expired keys in 3 batches");
        (await KeyExistsAsync(expired)).ShouldBeFalse();
        (await KeyExistsAsync(almostExpired)).ShouldBeTrue();
        (await _database.ScalarAsync<long>("postgres", "SELECT count(*) FROM customers.idempotency_keys WHERE client_id = 'purge-customers-batch'"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task PurgeFunctionIsBoundedAndPrivate()
    {
        var definition = await _database.ColumnAsync("postgres", """
            SELECT concat_ws('|', prosecdef::text, pg_get_userbyid(proowner), array_to_string(proconfig, ','))
            FROM pg_proc WHERE oid = 'customers.purge_expired_idempotency_keys(integer)'::regprocedure
            """);
        var executors = await _database.ColumnAsync("postgres", """
            SELECT role
            FROM unnest(ARRAY['customers_app', 'customers_worker', 'ledger_app', 'ledger_worker']) AS role
            WHERE has_function_privilege(role, 'customers.purge_expired_idempotency_keys(integer)', 'EXECUTE')
            """);
        var publicExecute = await _database.ScalarAsync<bool>("postgres", """
            SELECT EXISTS (
                SELECT 1 FROM pg_proc, aclexplode(coalesce(proacl, acldefault('f', proowner))) AS acl
                WHERE oid = 'customers.purge_expired_idempotency_keys(integer)'::regprocedure AND acl.grantee = 0
                  AND acl.privilege_type = 'EXECUTE')
            """);
        var zero = await _database.SqlStateOfAsync("customers_worker", "SELECT customers.purge_expired_idempotency_keys(0)");
        var tooLarge = await _database.SqlStateOfAsync("customers_worker", "SELECT customers.purge_expired_idempotency_keys(10001)");

        definition.ShouldBe(["true|migrator|search_path=customers, pg_temp"]);
        executors.ShouldBe(["customers_worker"]);
        publicExecute.ShouldBeFalse();
        zero.ShouldBe(InvalidParameterValue);
        tooLarge.ShouldBe(InvalidParameterValue);
    }

    private async Task<bool> KeyExistsAsync(Guid key) =>
        await _database.ScalarAsync<long>("postgres", $"SELECT count(*) FROM customers.idempotency_keys WHERE idempotency_key = '{key}'") == 1;
}
