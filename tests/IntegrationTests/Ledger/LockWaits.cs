using IntegrationTests.Infrastructure;

namespace IntegrationTests.Ledger;

internal static class LockWaits
{
    // Só a desistência: a barreira é o `pg_locks`, nunca um orçamento de relógio.
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    // Requisições da Api paradas em lock, vistas em `pg_locks` como pedido não concedido.
    internal static Task WaitForBlockedRequestsAsync(this PostgresDatabase database, int count) =>
        Eventually.WaitUntilAsync(
            async () => await database.ScalarAsync<long>("postgres", """
                SELECT count(DISTINCT l.pid)
                FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
                WHERE NOT l.granted AND a.usename = 'ledger_app'
                """) >= count,
            Ceiling,
            () => $"{count} requests never blocked on a lock");
}
