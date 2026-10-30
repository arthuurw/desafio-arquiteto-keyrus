using IntegrationTests.Infrastructure;

namespace IntegrationTests.Ledger;

[Collection(MigratedDatabaseDefinition.Name)]
public sealed class IdempotencyKeyPurgeTests(MigratedDatabase fixture)
{
    private const string InsufficientPrivilege = "42501";
    private const string Purged = "Idempotency key purge removed";
    private const string Failed = "Idempotency key purge failed";
    private static readonly TimeSpan LogTimeout = TimeSpan.FromSeconds(30);

    private readonly PostgresDatabase _database = fixture.Database;

    [Fact]
    public async Task WorkerPurgesExpiredKeysOnStartup()
    {
        var (expired, almostExpired, fresh) = await SeedThreeKeysAsync();

        using var worker = WorkerProcess.Start(_database.ConnectionStringFor("ledger_app", pooling: true));
        await worker.WaitForOutputAsync(output => output.Contains(Purged, StringComparison.Ordinal), LogTimeout);

        worker.Output.ShouldContain($"{Purged} 1 expired keys");
        (await ExistsAsync(expired)).ShouldBeFalse();
        (await ExistsAsync(almostExpired)).ShouldBeTrue();
        (await ExistsAsync(fresh)).ShouldBeTrue();
    }

    [Fact]
    public async Task WorkerRepeatsPurgeEachInterval()
    {
        using (var defaults = WorkerProcess.Start(_database.ConnectionStringFor("ledger_app", pooling: true)))
        {
            await defaults.WaitForOutputAsync(output => output.Contains(Purged, StringComparison.Ordinal), LogTimeout);
            defaults.Output.ShouldContain("Idempotency key purge scheduled every 01:00:00");
        }

        using var worker = WorkerProcess.Start(_database.ConnectionStringFor("ledger_app", pooling: true), TimeSpan.FromSeconds(1));
        await worker.WaitForOutputAsync(output => output.Contains(Purged, StringComparison.Ordinal), LogTimeout);
        var expired = await SeedKeyAsync("7 days 1 minute");
        await WaitUntilGoneAsync(expired);
        await worker.WaitForOutputAsync(output => WorkerProcess.Occurrences(output, Purged) >= 2, LogTimeout);

        worker.Output.ShouldContain("Idempotency key purge scheduled every 00:00:01");
        WorkerProcess.Occurrences(worker.Output, Purged).ShouldBeGreaterThanOrEqualTo(2);
        (await ExistsAsync(expired)).ShouldBeFalse();
    }

    [Fact]
    public async Task AppRoleCannotDeleteKeys()
    {
        var delete = await _database.SqlStateOfAsync("ledger_app", "DELETE FROM ledger.idempotency_keys");
        var truncate = await _database.SqlStateOfAsync("ledger_app", "TRUNCATE ledger.idempotency_keys");

        delete.ShouldBe(InsufficientPrivilege);
        truncate.ShouldBe(InsufficientPrivilege);
    }

    [Fact]
    public async Task PurgeFunctionDeletesOnlyExpiredKeys()
    {
        var definition = await _database.ColumnAsync("postgres", """
            SELECT p.pronargs::text || ',' || p.prosecdef::text || ',' || pg_get_userbyid(p.proowner)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'ledger' AND p.proname = 'purge_expired_idempotency_keys'
            """);
        var ledgerApp = await _database.ScalarAsync<bool>(
            "postgres", "SELECT has_function_privilege('ledger_app', 'ledger.purge_expired_idempotency_keys()', 'EXECUTE')");
        // `customers_app` não recebeu nada: só teria EXECUTE se ele valesse para PUBLIC.
        var otherRole = await _database.ScalarAsync<bool>(
            "postgres", "SELECT has_function_privilege('customers_app', 'ledger.purge_expired_idempotency_keys()', 'EXECUTE')");
        var (expired, almostExpired, fresh) = await SeedThreeKeysAsync();

        var purged = await _database.ScalarAsync<long>("ledger_app", "SELECT ledger.purge_expired_idempotency_keys()");

        definition.ShouldBe(["0,true,migrator"]);
        ledgerApp.ShouldBeTrue();
        otherRole.ShouldBeFalse();
        purged.ShouldBe(1);
        (await ExistsAsync(expired)).ShouldBeFalse();
        (await ExistsAsync(almostExpired)).ShouldBeTrue();
        (await ExistsAsync(fresh)).ShouldBeTrue();
    }

    [Fact]
    public async Task WorkerSurvivesPurgeFailure()
    {
        using var worker = WorkerProcess.Start(UnreachableDatabase.ConnectionString, TimeSpan.FromSeconds(1));
        await worker.WaitForOutputAsync(output => WorkerProcess.Occurrences(output, Failed) >= 2, LogTimeout);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{worker.Port}") };

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        WorkerProcess.Occurrences(worker.Output, Failed).ShouldBeGreaterThanOrEqualTo(2);
        worker.Output.ShouldContain("fail: Ledger.Infrastructure.Persistence.IdempotencyKeyPurge");
        worker.HasExited.ShouldBeFalse();
        ((int)response.StatusCode).ShouldBe(200);
    }

    // Chaves de 7 dias e 1 minuto, de 6 dias, 23 h e 59 min, e de agora: só a primeira passou do prazo.
    private async Task<(Guid Expired, Guid AlmostExpired, Guid Fresh)> SeedThreeKeysAsync() =>
        (await SeedKeyAsync("7 days 1 minute"), await SeedKeyAsync("6 days 23 hours 59 minutes"), await SeedKeyAsync("0 seconds"));

    private async Task<Guid> SeedKeyAsync(string age)
    {
        var key = Guid.NewGuid();
        await _database.ExecuteAsync("postgres", $$"""
            INSERT INTO ledger.idempotency_keys (client_id, idempotency_key, request_hash, response_status, response_body, created_at)
            VALUES ('purge-test', '{{key}}', '\x00', 201, '{}', now() - interval '{{age}}')
            """);
        return key;
    }

    private async Task<bool> ExistsAsync(Guid key) =>
        await _database.ScalarAsync<bool>("postgres", $"SELECT EXISTS (SELECT 1 FROM ledger.idempotency_keys WHERE idempotency_key = '{key}')");

    // A rodada seguinte do Worker é o que apaga a chave: espera pelo efeito no banco, com teto.
    private async Task WaitUntilGoneAsync(Guid key)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (await ExistsAsync(key))
        {
            clock.Elapsed.ShouldBeLessThan(LogTimeout, $"key {key} was never purged");
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }
    }
}
