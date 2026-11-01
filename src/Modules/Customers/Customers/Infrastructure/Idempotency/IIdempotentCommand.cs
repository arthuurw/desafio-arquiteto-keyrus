using BuildingBlocks.Idempotency;
using Mediator;

namespace Customers.Infrastructure.Idempotency;

// Comando de escrita de Customers que o behavior executa uma vez por (client, chave) (ADR-0037). Própria do módulo:
// o behavior de Customers se restringe a ela e nunca envolve um comando do Ledger.
internal interface IIdempotentCommand : IIdempotentRequest, ICommand<WriteResult>;

// Primeiro campo de `RequestFields`: a mesma chave em outra operação nunca vira repetição.
internal static class IdempotentOperation
{
    internal const string RegisterCustomer = "register-customer";
}
