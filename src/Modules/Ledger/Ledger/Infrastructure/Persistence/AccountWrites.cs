using BuildingBlocks.Identity;
using Ledger.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Infrastructure.Persistence;

// Transação de escrita do ADR-0005: trava a conta, o agregado decide, grava e confirma; qualquer recusa desfaz tudo.
internal static class AccountWrites
{
    private const string LockNotAvailable = "55P03";

    internal static async Task<Outcome<T>> WriteAsync<T>(
        this LedgerDbContext db, Guid accountId, Caller caller, Func<Account, Task<Outcome<T>>> decide, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // Espera limitada na fila do lock: esgotada, a requisição falha rápido e o chamador repete (ADR-0021).
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);

            // Sem composição: o FOR UPDATE fica na consulta principal, não num subselect gerado pelo EF.
            // No modo delegado o filtro de titular vai no próprio lock: o PostgreSQL não trava nem espera a linha
            // que o filtro descarta, então a conta de outro titular responde como inexistente, sem revelar,
            // nem pelo tempo de espera, que existe (ADR-0013).
            var query = caller.Mode == CallerMode.Delegated
                ? db.Accounts.FromSql(
                    $"SELECT * FROM ledger.accounts WHERE id = {accountId} AND customer_id = {caller.CustomerId} FOR UPDATE")
                : db.Accounts.FromSql($"SELECT * FROM ledger.accounts WHERE id = {accountId} FOR UPDATE");
            var account = (await query.ToListAsync(cancellationToken)).SingleOrDefault();

            if (account is null)
            {
                return LedgerError.AccountNotFound;
            }

            var outcome = await decide(account);
            if (!outcome.IsSuccess)
            {
                return outcome;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return outcome;
        }
        catch (Exception exception) when (IsLockTimeout(exception))
        {
            return LedgerError.ConcurrencyTimeout;
        }
    }

    // Lido depois do lock: `clock_timestamp()` é o instante real, `now()` seria o início da transação.
    // `GREATEST` mantém a ordem mesmo se o relógio do servidor voltar (ADR-0005).
    internal static Task<DateTimeOffset> RecordingTimeAsync(this LedgerDbContext db, Guid accountId, CancellationToken cancellationToken) =>
        db.Database
            .SqlQuery<DateTimeOffset>($"SELECT GREATEST(clock_timestamp(), last_recorded_at) AS \"Value\" FROM ledger.accounts WHERE id = {accountId}")
            .SingleAsync(cancellationToken);

    private static bool IsLockTimeout(Exception exception) =>
        exception is PostgresException { SqlState: LockNotAvailable }
        || exception.InnerException is PostgresException { SqlState: LockNotAvailable };
}
