using IntegrationTests.Infrastructure;
using Npgsql;

namespace IntegrationTests.Ledger;

// As funções do Worker sobre o outbox, chamadas como `ledger_worker` contra o banco migrado (ADR-0035).
[Collection(MigratedDatabaseDefinition.Name)]
public sealed class OutboxFunctionTests(MigratedDatabase fixture)
{
    private const string InvalidParameter = "22023";

    private readonly PostgresDatabase _database = fixture.Database;

    [Theory]
    [InlineData("claim_outbox_batch(integer, bigint)")]
    [InlineData("mark_outbox_processed(bigint[])")]
    [InlineData("purge_processed_outbox(integer)")]
    public async Task FunctionIsSecurityDefinerForWorkerOnly(string signature)
    {
        var function = $"ledger.{signature}";
        var definition = await _database.ColumnAsync("postgres", $"""
            SELECT concat_ws('|', prosecdef::text, pg_get_userbyid(proowner), array_to_string(proconfig, ','))
            FROM pg_proc WHERE oid = '{function}'::regprocedure
            """);
        var grantees = await _database.ColumnAsync("postgres", $"""
            SELECT CASE WHEN acl.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END
            FROM pg_proc, aclexplode(proacl) AS acl
            WHERE oid = '{function}'::regprocedure AND acl.privilege_type = 'EXECUTE'
            ORDER BY 1
            """);
        var app = await _database.ScalarAsync<bool>("postgres", $"SELECT has_function_privilege('ledger_app', '{function}', 'EXECUTE')");

        definition.ShouldBe(["true|migrator|search_path=ledger, pg_temp"]);
        grantees.ShouldBe(["ledger_worker", "migrator"]);
        app.ShouldBeFalse();
    }

    [Fact]
    public async Task ClaimSkipsEventsLockedByAnotherTransaction()
    {
        var first = await _database.SeedEventAsync(processedAgo: null);
        var second = await _database.SeedEventAsync(processedAgo: null);
        var third = await _database.SeedEventAsync(processedAgo: null);

        // O Worker não lê a tabela: o lock do evento vem de uma transação de superusuário.
        await using var superuser = new NpgsqlConnection(_database.ConnectionStringFor("postgres"));
        await superuser.OpenAsync(TestContext.Current.CancellationToken);
        await using var held = await superuser.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var command = new NpgsqlCommand($"SELECT id FROM ledger.outbox WHERE id = {first} FOR UPDATE", superuser, held))
        {
            (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(first);
        }

        await using var worker = await WorkerTransaction.OpenAsync(_database);
        // Sem SKIP LOCKED, a reserva esperaria o lock e esgotaria o `lock_timeout`.
        await worker.ExecuteAsync("SET LOCAL lock_timeout = '1s'");
        var claimed = await worker.ClaimAsync(1000);

        // Uma segunda réplica não recebe o que a primeira reservou.
        await using var replica = await WorkerTransaction.OpenAsync(_database);
        var replicaClaimed = await replica.ClaimAsync(1000);

        claimed.ShouldNotContain(first);
        claimed.ShouldContain(second);
        claimed.ShouldContain(third);
        claimed.ShouldBe(claimed.Order().ToList());
        replicaClaimed.ShouldNotContain(first);
        replicaClaimed.ShouldNotContain(second);
        replicaClaimed.ShouldNotContain(third);
    }

    [Fact]
    public async Task ClaimStartsAfterTheCursor()
    {
        var first = await _database.SeedEventAsync(processedAgo: null);
        var second = await _database.SeedEventAsync(processedAgo: null);
        var third = await _database.SeedEventAsync(processedAgo: null);

        await using var worker = await WorkerTransaction.OpenAsync(_database);
        var claimed = await worker.ClaimAsync(1000, after: first);

        claimed.ShouldNotContain(first);
        claimed.ShouldContain(second);
        claimed.ShouldContain(third);
        claimed.ShouldAllBe(id => id > first);
    }

    [Theory]
    [InlineData("claim_outbox_batch", "0, 0", InvalidParameter)]
    [InlineData("claim_outbox_batch", "1, 0", null)]
    [InlineData("claim_outbox_batch", "1000, 0", null)]
    [InlineData("claim_outbox_batch", "1001, 0", InvalidParameter)]
    [InlineData("purge_processed_outbox", "0", InvalidParameter)]
    [InlineData("purge_processed_outbox", "1", null)]
    [InlineData("purge_processed_outbox", "10000", null)]
    [InlineData("purge_processed_outbox", "10001", InvalidParameter)]
    [InlineData("claim_outbox_batch", "1, -1", InvalidParameter)]
    [InlineData("claim_outbox_batch", "1, NULL", InvalidParameter)]
    public async Task BatchSizeIsBounded(string function, string arguments, string? expected)
    {
        // Numa transação desfeita: a chamada aceita não deixa efeito para os outros testes.
        await using var worker = await WorkerTransaction.OpenAsync(_database);
        string? sqlState = null;
        try
        {
            await worker.ExecuteAsync($"SELECT count(*) FROM ledger.{function}({arguments})");
        }
        catch (PostgresException exception)
        {
            sqlState = exception.SqlState;
        }

        sqlState.ShouldBe(expected);
    }

    [Fact]
    public async Task MarkKeepsFirstProcessedAt()
    {
        var processed = await _database.SeedEventAsync(processedAgo: "1 day");
        var pending = await _database.SeedEventAsync(processedAgo: null);
        var before = await ProcessedAtAsync(processed);

        long marked;
        await using (var worker = await WorkerTransaction.OpenAsync(_database))
        {
            marked = await worker.ScalarAsync<long>($"SELECT ledger.mark_outbox_processed(ARRAY[{processed}, {pending}]::bigint[])");
            await worker.CommitAsync();
        }

        marked.ShouldBe(1);

        (await ProcessedAtAsync(processed)).ShouldBe(before);
        (await ProcessedAtAsync(pending)).ShouldNotBeNull();
    }

    private Task<DateTime?> ProcessedAtAsync(long id) =>
        _database.ScalarAsync<DateTime?>("postgres", $"SELECT processed_at FROM ledger.outbox WHERE id = {id}");
}

// Transação aberta como `ledger_worker`, desfeita no descarte se não for confirmada.
internal sealed class WorkerTransaction : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private WorkerTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    internal static async Task<WorkerTransaction> OpenAsync(PostgresDatabase database)
    {
        var connection = new NpgsqlConnection(database.ConnectionStringFor("ledger_worker"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return new WorkerTransaction(connection, await connection.BeginTransactionAsync(TestContext.Current.CancellationToken));
    }

    internal async Task ExecuteAsync(string sql)
    {
        await using var command = new NpgsqlCommand(sql, _connection, _transaction);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    internal async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = new NpgsqlCommand(sql, _connection, _transaction);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    internal async Task<List<long>> ClaimAsync(int batchSize, long after = 0)
    {
        await using var command = new NpgsqlCommand($"SELECT id FROM ledger.claim_outbox_batch({batchSize}, {after})", _connection, _transaction);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var ids = new List<long>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    internal Task CommitAsync() => _transaction.CommitAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
