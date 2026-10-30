using Ledger.Domain;
using Ledger.Infrastructure.Persistence;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Features.Idempotency;

// Executa cada comando idempotente uma vez por (client, chave): chave, operação e resposta confirmam na mesma
// transação, então não existe operação feita sem chave registrada, nem o inverso (ADR-0014).
internal sealed class IdempotencyBehavior<TMessage, TResponse>(LedgerDbContext db) : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IIdempotentCommand
{
    public async ValueTask<TResponse> Handle(
        TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        // Todo IIdempotentCommand responde WriteResult; o Mediator só não expõe isso no tipo genérico.
        var result = await ExecuteOnceAsync(
            message, async () => (WriteResult)(object)(await next(message, cancellationToken))!, cancellationToken);
        return (TResponse)(object)result;
    }

    private async Task<WriteResult> ExecuteOnceAsync(TMessage message, Func<Task<WriteResult>> execute, CancellationToken cancellationToken)
    {
        var clientId = message.Caller.ClientId;
        var key = message.IdempotencyKey;
        var hash = RequestHash.Of(message);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // Vale para a espera pela chave e, depois, pelo lock da conta (ADR-0021).
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);

            // Com a mesma chave numa transação aberta, o PostgreSQL faz este INSERT esperar o desfecho dela:
            // confirmada, nada é inserido e vale a resposta dela; desfeita, a inserção prossegue.
            var inserted = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO ledger.idempotency_keys (client_id, idempotency_key, request_hash)
                VALUES ({clientId}, {key}, {hash})
                ON CONFLICT DO NOTHING
                """,
                cancellationToken);
            if (inserted == 0)
            {
                var stored = await db.Database
                    .SqlQuery<StoredResponse>(
                        $"""
                        SELECT request_hash AS "RequestHash", response_status AS "Status", response_body::text AS "Body"
                        FROM ledger.idempotency_keys
                        WHERE client_id = {clientId} AND idempotency_key = {key}
                        """)
                    .SingleAsync(cancellationToken);
                return stored.RequestHash.AsSpan().SequenceEqual(hash)
                    ? new WriteResult(stored.Status, stored.Body) { Replayed = true }
                    : WriteResult.Problem(LedgerProblems.IdempotencyKeyReused());
            }

            var result = await execute();
            if (result.IsTransient)
            {
                // Sem commit: o descarte da transação desfaz a chave junto, e a repetição executa de novo.
                return result;
            }

            await db.Database.ExecuteSqlAsync(
                $"""
                UPDATE ledger.idempotency_keys
                SET response_status = {result.Status}, response_body = {result.Body}::jsonb
                WHERE client_id = {clientId} AND idempotency_key = {key}
                """,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception exception) when (AccountWrites.IsLockTimeout(exception))
        {
            return WriteResult.Rejected(LedgerError.ConcurrencyTimeout);
        }
    }

    // Só chave confirmada é encontrada aqui, e ela sempre tem resposta.
    private sealed record StoredResponse(byte[] RequestHash, int Status, string Body);
}
