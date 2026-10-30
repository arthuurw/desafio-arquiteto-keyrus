using System.Diagnostics;
using IntegrationTests.Infrastructure;

namespace IntegrationTests.Ledger;

internal static class LockWaits
{
    // Espera por estado observável, não por tempo: requisições da Api paradas em lock no PostgreSQL.
    // O teto fica abaixo do `lock_timeout` de 2 s, para o teste soltar o lock antes de a Api desistir.
    internal static async Task WaitForBlockedRequestsAsync(this PostgresDatabase database, int count)
    {
        var deadline = Stopwatch.StartNew();
        while (await database.ScalarAsync<long>(
                   "postgres", "SELECT count(*) FROM pg_stat_activity WHERE usename = 'ledger_app' AND wait_event_type = 'Lock'") < count)
        {
            deadline.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1.5), $"{count} requests never blocked on a lock");
            await Task.Yield();
        }
    }
}
