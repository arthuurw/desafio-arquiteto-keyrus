using IntegrationTests.Infrastructure;

namespace IntegrationTests.Ledger;

[Collection(MigratedDatabaseDefinition.Name)]
public sealed class IdempotencyKeyPurgeTests(MigratedDatabase fixture)
{
    private const string InsufficientPrivilege = "42501";
    private const string InvalidParameterValue = "22023";
    private const string Purged = "Idempotency key purge removed";
    private const string Failed = "Idempotency key purge failed";
    private static readonly TimeSpan LogTimeout = TimeSpan.FromSeconds(30);

    private readonly PostgresDatabase _database = fixture.Database;

    [Fact]
    public async Task WorkerPurgesExpiredKeysOnStartup()
    {
        await ClearExpiredKeysAsync();
        var (expired, almostExpired, fresh) = await SeedThreeKeysAsync();

        using var worker = WorkerProcess.Start(WorkerConnectionString);
        await worker.WaitForOutputAsync(output => output.Contains(Purged, StringComparison.Ordinal), LogTimeout);

        worker.Output.ShouldContain($"{Purged} 1 expired keys");
        (await ExistsAsync(expired)).ShouldBeFalse();
        (await ExistsAsync(almostExpired)).ShouldBeTrue();
        (await ExistsAsync(fresh)).ShouldBeTrue();
    }

    [Fact]
    public async Task WorkerPurgesInBatches()
    {
        await ClearExpiredKeysAsync();
        await _database.ExecuteAsync("postgres", """
            INSERT INTO ledger.idempotency_keys (client_id, idempotency_key, request_hash, response_status, response_body, created_at)
            SELECT 'purge-batch', gen_random_uuid(), '\x00', 201, '{}', now() - interval '8 days' FROM generate_series(1, 2500)
            """);
        await _database.ExecuteAsync("postgres", """
            INSERT INTO ledger.idempotency_keys (client_id, idempotency_key, request_hash, response_status, response_body)
            SELECT 'purge-batch', gen_random_uuid(), '\x00', 201, '{}' FROM generate_series(1, 3)
            """);
        // Conta as chamadas da função nas sessões do Worker, que abrem depois desta configuração.
        await _database.ExecuteAsync("postgres", "ALTER ROLE ledger_worker SET track_functions = 'pl'");
        var callsBefore = await PurgeCallsAsync();

        using var worker = WorkerProcess.Start(WorkerConnectionString);
        await worker.WaitForOutputAsync(output => output.Contains(Purged, StringComparison.Ordinal), LogTimeout);
        // As estatísticas da sessão chegam ao contador compartilhado com atraso de alguns segundos.
        await Eventually.WaitUntilAsync(
            async () => await PurgeCallsAsync() - callsBefore >= 3,
            LogTimeout,
            () => "the purge function was never called 3 times",
            TimeSpan.FromMilliseconds(200));

        worker.Output.ShouldContain($"{Purged} 2500 expired keys in 3 batches");
        (await _database.ScalarAsync<long>("postgres", "SELECT count(*) FROM ledger.idempotency_keys WHERE created_at < now() - interval '7 days'"))
            .ShouldBe(0);
        (await _database.ScalarAsync<long>("postgres", "SELECT count(*) FROM ledger.idempotency_keys WHERE client_id = 'purge-batch'"))
            .ShouldBe(3);
        // Lotes de 1000: três chamadas para 2500 chaves só se as duas primeiras vierem cheias e a última com 500.
        (await PurgeCallsAsync() - callsBefore).ShouldBe(3);
    }

    [Fact]
    public async Task WorkerConnectsAsWorkerRole()
    {
        using var worker = WorkerProcess.Start(WorkerConnectionString);
        await worker.WaitForOutputAsync(output => output.Contains(Purged, StringComparison.Ordinal), LogTimeout);

        // O pool mantém a sessão aberta depois da rodada.
        var sessions = await _database.ColumnAsync("postgres", """
            SELECT usename::text FROM pg_stat_activity WHERE datname = 'ledger_db' AND usename IN ('ledger_worker', 'ledger_app')
            """);

        sessions.ShouldNotBeEmpty();
        sessions.ShouldAllBe(user => user == "ledger_worker");
    }

    [Fact]
    public async Task WorkerRepeatsPurgeEachInterval()
    {
        using (var defaults = WorkerProcess.Start(WorkerConnectionString))
        {
            await defaults.WaitForOutputAsync(output => output.Contains(Purged, StringComparison.Ordinal), LogTimeout);
            defaults.Output.ShouldContain("Idempotency key purge scheduled every 01:00:00");
        }

        using var worker = WorkerProcess.Start(WorkerConnectionString, TimeSpan.FromSeconds(1));
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
        await ClearExpiredKeysAsync();
        var definition = await _database.ColumnAsync("postgres", """
            SELECT p.pronargs::text || ',' || p.prosecdef::text || ',' || pg_get_userbyid(p.proowner)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'ledger' AND p.proname = 'purge_expired_idempotency_keys'
            """);
        var executors = await _database.ColumnAsync("postgres", """
            SELECT r.rolname::text FROM pg_roles r
            WHERE r.rolname IN ('ledger_worker', 'ledger_app', 'customers_app', 'migrator')
              AND has_function_privilege(r.rolname, 'ledger.purge_expired_idempotency_keys(integer)', 'EXECUTE')
            ORDER BY r.rolname
            """);
        var sixDays = await SeedKeyAsync("6 days 23 hours 59 minutes");
        var eightDays = await SeedKeyAsync("8 days");

        var purged = await _database.ScalarAsync<long>("ledger_worker", "SELECT ledger.purge_expired_idempotency_keys(1000)");

        definition.ShouldBe(["1,true,migrator"]);
        executors.ShouldBe(["ledger_worker", "migrator"]);
        purged.ShouldBe(1);
        (await ExistsAsync(eightDays)).ShouldBeFalse();
        (await ExistsAsync(sixDays)).ShouldBeTrue();
    }

    [Fact]
    public async Task PurgeFunctionBoundsBatch()
    {
        await ClearExpiredKeysAsync();
        for (var i = 0; i < 5; i++)
        {
            await SeedKeyAsync("8 days");
        }

        var purged = await _database.ScalarAsync<long>("ledger_worker", "SELECT ledger.purge_expired_idempotency_keys(2)");
        var remaining = await ExpiredCountAsync();
        var zero = await _database.SqlStateOfAsync("ledger_worker", "SELECT ledger.purge_expired_idempotency_keys(0)");
        var aboveMax = await _database.SqlStateOfAsync("ledger_worker", "SELECT ledger.purge_expired_idempotency_keys(10001)");

        purged.ShouldBe(2);
        remaining.ShouldBe(3);
        zero.ShouldBe(InvalidParameterValue);
        aboveMax.ShouldBe(InvalidParameterValue);
        (await ExpiredCountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task WorkerSurvivesPurgeFailure()
    {
        using var worker = WorkerProcess.Start(UnreachableDatabase.ConnectionString, TimeSpan.FromSeconds(1));
        await worker.WaitForOutputAsync(output => WorkerProcess.Occurrences(output, Failed) >= 2, LogTimeout);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{worker.Port}") };

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        WorkerProcess.Occurrences(worker.Output, Failed).ShouldBeGreaterThanOrEqualTo(2);
        worker.Output.ShouldContain("fail: Ledger.Infrastructure.Idempotency.IdempotencyKeyPurge");
        worker.HasExited.ShouldBeFalse();
        ((int)response.StatusCode).ShouldBe(200);
    }

    private string WorkerConnectionString => _database.ConnectionStringFor("ledger_worker", pooling: true);

    // O banco é da coleção: chaves vencidas deixadas por outro teste entrariam na contagem deste.
    private Task ClearExpiredKeysAsync() =>
        _database.ExecuteAsync("postgres", "DELETE FROM ledger.idempotency_keys WHERE created_at < now() - interval '7 days'");

    private Task<long> ExpiredCountAsync() =>
        _database.ScalarAsync<long>("postgres", "SELECT count(*) FROM ledger.idempotency_keys WHERE created_at < now() - interval '7 days'");

    private Task<long> PurgeCallsAsync() =>
        _database.ScalarAsync<long>("postgres", """
            SELECT coalesce(sum(calls), 0)::bigint FROM pg_stat_user_functions
            WHERE schemaname = 'ledger' AND funcname = 'purge_expired_idempotency_keys'
            """);

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
    private Task WaitUntilGoneAsync(Guid key) =>
        Eventually.WaitUntilAsync(
            async () => !await ExistsAsync(key), LogTimeout, () => $"key {key} was never purged", TimeSpan.FromMilliseconds(100));
}
