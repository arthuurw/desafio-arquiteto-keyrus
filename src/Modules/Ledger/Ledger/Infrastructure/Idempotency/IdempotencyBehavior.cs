using Ledger.Domain;
using Ledger.Features;
using Ledger.Infrastructure.Persistence;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure.Idempotency;

// Executa cada comando idempotente uma vez por (client, chave): chave, operação e resposta confirmam na mesma
// transação, então não existe operação feita sem chave registrada, nem o inverso (ADR-0014).
// O gerador do Mediator só registra behavior aberto em <TMessage, TResponse>; TResponse é sempre WriteResult,
// garantido por IIdempotentCommand, e a conversão fica restrita às duas pontas.
internal sealed class IdempotencyBehavior<TMessage, TResponse>(LedgerDbContext db) : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IIdempotentCommand
{
    public async ValueTask<TResponse> Handle(
        TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken) =>
        (TResponse)(object)await ExecuteOnceAsync(message, async () => (WriteResult)(object)(await next(message, cancellationToken))!, cancellationToken);

    private async Task<WriteResult> ExecuteOnceAsync(TMessage message, Func<Task<WriteResult>> execute, CancellationToken cancellationToken)
    {
        var clientId = message.Caller.ClientId;
        var key = message.IdempotencyKey;
        var hash = RequestHash.Of(message);
        try
        {
            // O `lock_timeout` vale para a espera pela chave e, depois, pelo lock da conta.
            await using var transaction = await db.BeginWriteTransactionAsync(cancellationToken);

            var stored = await ClaimKeyAsync(clientId, key, hash, cancellationToken);
            if (stored is not null)
            {
                return stored.RequestHash.AsSpan().SequenceEqual(hash)
                    ? new WriteResult(stored.Status, stored.Body) { Replayed = true }
                    : WriteResult.Problem(LedgerProblems.IdempotencyKeyReused());
            }

            var result = await execute();
            if (IsTransient(result))
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
            return WriteResult.Refused(LedgerError.ConcurrencyTimeout);
        }
    }

    // Registra a chave nesta transação e devolve null, ou devolve a resposta já confirmada com ela.
    private async Task<StoredResponse?> ClaimKeyAsync(string clientId, Guid key, byte[] hash, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Com a mesma chave numa transação aberta, o PostgreSQL faz este INSERT esperar o desfecho dela:
            // confirmada, nada é inserido e vale a resposta dela; desfeita, a inserção prossegue.
            var inserted = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO ledger.idempotency_keys (client_id, idempotency_key, request_hash)
                VALUES ({clientId}, {key}, {hash})
                ON CONFLICT DO NOTHING
                """,
                cancellationToken);
            if (inserted == 1)
            {
                return null;
            }

            var stored = await db.Database
                .SqlQuery<StoredResponse>(
                    $"""
                    SELECT request_hash AS "RequestHash", response_status AS "Status", response_body::text AS "Body"
                    FROM ledger.idempotency_keys
                    WHERE client_id = {clientId} AND idempotency_key = {key}
                    """)
                .SingleOrDefaultAsync(cancellationToken);
            // A limpeza pode apagar a chave entre o conflito e esta leitura: a chave é registrada de novo, e a
            // operação executa como nova.
            if (stored is not null)
            {
                return stored;
            }
        }
    }

    // Lock esgotado é a única recusa que repetir pode mudar; as demais são guardadas e devolvidas iguais.
    private static bool IsTransient(WriteResult result) => result.Error == LedgerError.ConcurrencyTimeout;

    // Só chave confirmada é encontrada aqui, e ela sempre tem resposta.
    private sealed record StoredResponse(byte[] RequestHash, int Status, string Body);
}
