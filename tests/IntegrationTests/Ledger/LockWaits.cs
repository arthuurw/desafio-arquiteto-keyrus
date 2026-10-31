using System.Diagnostics;
using IntegrationTests.Infrastructure;

namespace IntegrationTests.Ledger;

internal static class LockWaits
{
    // Abaixo do `lock_timeout` de 2 s: o teste solta o lock antes de a Api desistir. A espera real é de milissegundos.
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(1.5);

    internal static Task WaitForBlockedRequestsAsync(this PostgresDatabase database, int count) =>
        database.WaitForBlockedRequestsAsync(count, Ceiling);

    // Um teto só para todas as esperas de um teste: somadas, elas ficam abaixo do `lock_timeout`.
    internal static LockWaitBudget LockWaitBudget(this PostgresDatabase database) => new(database, Ceiling);

    // Requisições da Api paradas em lock, vistas em `pg_locks` como pedido não concedido.
    internal static Task WaitForBlockedRequestsAsync(this PostgresDatabase database, int count, TimeSpan ceiling) =>
        Eventually.WaitUntilAsync(
            async () => await database.ScalarAsync<long>("postgres", """
                SELECT count(DISTINCT l.pid)
                FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
                WHERE NOT l.granted AND a.usename = 'ledger_app'
                """) >= count,
            ceiling,
            () => $"{count} requests never blocked on a lock");
}

internal sealed class LockWaitBudget(PostgresDatabase database, TimeSpan total)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    internal Task WaitForBlockedRequestsAsync(int count) =>
        database.WaitForBlockedRequestsAsync(count, total - _clock.Elapsed);
}
