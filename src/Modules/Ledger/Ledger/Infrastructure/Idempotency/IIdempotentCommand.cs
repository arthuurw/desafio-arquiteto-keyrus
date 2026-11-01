using BuildingBlocks.Idempotency;
using Mediator;

namespace Ledger.Infrastructure.Idempotency;

// Comando de escrita do Ledger que o behavior executa uma vez por (client, chave) (ADR-0037). Própria do módulo:
// o behavior do Ledger se restringe a ela e nunca envolve um comando de outro módulo.
internal interface IIdempotentCommand : IIdempotentRequest, ICommand<WriteResult>;

// Primeiro campo de `RequestFields`: a mesma chave em outra operação nunca vira repetição.
internal static class IdempotentOperation
{
    internal const string OpenAccount = "open-account";
    internal const string Credit = "credit";
    internal const string Debit = "debit";
    internal const string Reversal = "reversal";
}
