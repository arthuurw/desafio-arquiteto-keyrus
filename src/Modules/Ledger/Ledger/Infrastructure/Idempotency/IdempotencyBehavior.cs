using BuildingBlocks.Audit;
using BuildingBlocks.Idempotency;
using Ledger.Domain;
using Ledger.Features;
using Ledger.Infrastructure.Persistence;
using Mediator;

namespace Ledger.Infrastructure.Idempotency;

// O fluxo de idempotência do BuildingBlocks fechado com a tabela, a transação e o hash do Ledger (ADR-0037).
// O gerador do Mediator só registra behavior aberto em <TMessage, TResponse>; TResponse é sempre WriteResult,
// garantido por IIdempotentCommand, e a conversão fica restrita às duas pontas.
internal sealed class IdempotencyBehavior<TMessage, TResponse>(LedgerDbContext db, AuditEvent audit) : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IIdempotentCommand
{
    public async ValueTask<TResponse> Handle(
        TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        // O `lock_timeout` vale para a espera pela chave e, depois, pelo lock da conta.
        var execution = new IdempotentExecution(db, "ledger.idempotency_keys", db.BeginWriteTransactionAsync, audit);
        try
        {
            var result = await execution.ExecuteOnceAsync(
                message,
                RequestHash.Of(message),
                async () => (WriteResult)(object)(await next(message, cancellationToken))!,
                cancellationToken);
            return (TResponse)(object)result;
        }
        catch (Exception exception) when (AccountWrites.IsLockTimeout(exception))
        {
            return (TResponse)(object)LedgerProblems.Refused(LedgerError.ConcurrencyTimeout);
        }
    }
}
