using IntegrationTests.Infrastructure;

namespace IntegrationTests.Ledger;

internal static class LockWaits
{
    // Requisições da Api paradas em lock no PostgreSQL. O teto fica abaixo do `lock_timeout` de 2 s,
    // para o teste soltar o lock antes de a Api desistir; a espera real é de milissegundos.
    internal static Task WaitForBlockedRequestsAsync(this PostgresDatabase database, int count) =>
        Eventually.WaitUntilAsync(
            async () => await database.ScalarAsync<long>(
                "postgres", "SELECT count(*) FROM pg_stat_activity WHERE usename = 'ledger_app' AND wait_event_type = 'Lock'") >= count,
            TimeSpan.FromSeconds(1.5),
            () => $"{count} requests never blocked on a lock");
}
