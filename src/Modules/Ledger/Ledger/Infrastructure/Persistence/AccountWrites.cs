using BuildingBlocks.Identity;
using Ledger.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ledger.Infrastructure.Persistence;

// Escrita do ADR-0005: trava a conta, o agregado decide e grava. Quem abriu a transação confirma ou desfaz:
// o handler da troca de limite, ou o behavior de idempotência, junto com a chave (ADR-0014).
internal static class AccountWrites
{
    private const string LockNotAvailable = "55P03";

    // Espera limitada em cada lock da transação: esgotada, a requisição falha rápido e o chamador repete (ADR-0021).
    internal static async Task<IDbContextTransaction> BeginWriteTransactionAsync(this LedgerDbContext db, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
        return transaction;
    }

    internal static async Task<Outcome<T>> WriteAsync<T>(
        this LedgerDbContext db, Guid accountId, Caller caller, Func<Account, Task<Outcome<T>>> decide, CancellationToken cancellationToken)
        where T : class
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Account writes run inside a write transaction.");
        }

        try
        {
            // Sem compor LINQ sobre o SQL: o FOR UPDATE fica na consulta principal, não num subselect do EF.
            // No modo delegado o filtro de titular vai no lock: a linha de outro titular não é travada nem esperada,
            // e a conta responde como inexistente sem revelar, nem pelo tempo, que existe (ADR-0013).
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

    internal static bool IsLockTimeout(Exception exception) =>
        exception is PostgresException { SqlState: LockNotAvailable }
        || exception.InnerException is PostgresException { SqlState: LockNotAvailable };
}
