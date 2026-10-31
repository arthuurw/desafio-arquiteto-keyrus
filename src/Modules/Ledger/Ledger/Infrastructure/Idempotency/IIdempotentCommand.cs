using BuildingBlocks.Identity;
using Ledger.Features;
using Mediator;

namespace Ledger.Infrastructure.Idempotency;

// Comando de escrita que o behavior executa uma vez por (client, chave) (ADR-0014).
internal interface IIdempotentCommand : ICommand<WriteResult>
{
    Guid IdempotencyKey { get; }

    Caller Caller { get; }

    // Operação e valores já interpretados que definem "a mesma requisição"; a formatação do corpo não entra.
    IReadOnlyList<string> RequestFields { get; }
}

// Primeiro campo de `RequestFields`: a mesma chave em outra operação nunca vira repetição.
internal static class IdempotentOperation
{
    internal const string OpenAccount = "open-account";
    internal const string Credit = "credit";
    internal const string Debit = "debit";
    internal const string Reversal = "reversal";
}
