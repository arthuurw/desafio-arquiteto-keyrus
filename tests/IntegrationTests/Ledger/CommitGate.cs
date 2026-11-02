using IntegrationTests.Infrastructure;
using Npgsql;

namespace IntegrationTests.Ledger;

// Trigger deferido de teste em `ledger.entries`, só para uma conta: a escrita chega ao COMMIT com o lock da conta e o
// `recorded_at` já carimbados e para ali, até o teste soltar um advisory lock. Soltar confirma a escrita; cancelar o
// processo que espera a desfaz. Descartar remove o trigger.
internal sealed class CommitGate : IAsyncDisposable
{
    private readonly PostgresDatabase _database;
    private readonly NpgsqlConnection _holder;
    private readonly string _name;
    private readonly int _key;
    private bool _held = true;

    private CommitGate(PostgresDatabase database, NpgsqlConnection holder, string name, int key)
    {
        _database = database;
        _holder = holder;
        _name = name;
        _key = key;
    }

    internal static async Task<CommitGate> CloseAsync(PostgresDatabase database, Guid accountId)
    {
        var key = Random.Shared.Next(1, int.MaxValue);
        var name = $"commit_gate_{Guid.NewGuid():N}";
        var holder = new NpgsqlConnection(database.ConnectionStringFor("postgres"));
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using (var lockCommand = new NpgsqlCommand($"SELECT pg_advisory_lock({key})", holder))
        {
            await lockCommand.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // `lock_timeout = 0` na função: a escrita espera o teste sem estourar o `lock_timeout` de 2 s da transação dela.
        await database.ExecuteAsync("postgres", $"""
            CREATE FUNCTION public.{name}() RETURNS trigger LANGUAGE plpgsql SET lock_timeout = 0 AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock({key});
                RETURN NULL;
            END
            $$;
            CREATE CONSTRAINT TRIGGER {name} AFTER INSERT ON ledger.entries DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW WHEN (NEW.account_id = '{accountId}') EXECUTE FUNCTION public.{name}();
            """);
        return new CommitGate(database, holder, name, key);
    }

    // A escrita parada no COMMIT, vista no `pg_locks` como pedido não concedido do advisory lock.
    internal Task WaitForWriterAsync() =>
        Eventually.WaitUntilAsync(
            async () => await WriterPidAsync() is not null,
            TimeSpan.FromSeconds(60),
            () => "the write never reached the commit gate");

    internal Task OpenAsync() => ReleaseAsync();

    // Cancela a escrita parada: o COMMIT falha e a transação dela é desfeita.
    internal async Task CancelWriterAsync()
    {
        var pid = (await WriterPidAsync()).ShouldNotBeNull();
        (await _database.ScalarAsync<bool>("postgres", $"SELECT pg_cancel_backend({pid})")).ShouldBeTrue();
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync();
        await _holder.DisposeAsync();
        await _database.ExecuteAsync("postgres", $"""
            DROP TRIGGER {_name} ON ledger.entries;
            DROP FUNCTION public.{_name}();
            """);
    }

    private Task<int?> WriterPidAsync() =>
        _database.ScalarAsync<int?>("postgres", $"""
            SELECT pid FROM pg_locks
            WHERE locktype = 'advisory' AND classid = 0 AND objid = {_key} AND objsubid = 1 AND NOT granted
            LIMIT 1
            """);

    private async Task ReleaseAsync()
    {
        if (!_held)
        {
            return;
        }

        await using var command = new NpgsqlCommand($"SELECT pg_advisory_unlock({_key})", _holder);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        _held = false;
    }
}
