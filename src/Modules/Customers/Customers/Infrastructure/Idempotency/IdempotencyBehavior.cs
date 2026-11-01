using BuildingBlocks.Audit;
using BuildingBlocks.Idempotency;
using Customers.Features;
using Customers.Infrastructure.Persistence;
using Customers.Infrastructure.Vault;
using Mediator;

namespace Customers.Infrastructure.Idempotency;

// O fluxo de idempotência do BuildingBlocks fechado com a tabela de Customers e o hash por HMAC no Transit: o array da
// requisição tem nome, CPF, nascimento e e-mail, e um SHA-256 sem chave deixaria testar CPFs (ADR-0037).
// O gerador do Mediator só registra behavior aberto em <TMessage, TResponse>; TResponse é sempre WriteResult.
internal sealed class IdempotencyBehavior<TMessage, TResponse>(CustomersDbContext db, IBlindIndexer hashes, AuditRecord audit)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IIdempotentCommand
{
    public async ValueTask<TResponse> Handle(
        TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        var execution = new IdempotentExecution(db, "customers.idempotency_keys", db.Database.BeginTransactionAsync, audit);
        try
        {
            // Antes da transação: com o cofre fora, a chave nem chega a ser tocada.
            var hash = await hashes.RequestAsync(RequestFields.Serialize(message), cancellationToken);
            var result = await execution.ExecuteOnceAsync(
                message,
                hash.Value,
                async () => (WriteResult)(object)(await next(message, cancellationToken))!,
                cancellationToken);
            return (TResponse)(object)result;
        }
        catch (PersonalDataUnavailableException)
        {
            // A transação, se aberta, foi descartada sem commit: nem a chave nem a linha ficam.
            return (TResponse)(object)CustomersProblems.Unavailable();
        }
    }
}
