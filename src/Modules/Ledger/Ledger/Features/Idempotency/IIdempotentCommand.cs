using BuildingBlocks.Identity;
using Mediator;

namespace Ledger.Features.Idempotency;

// Comando de escrita que o behavior executa uma vez por (client, chave) (ADR-0014).
internal interface IIdempotentCommand : ICommand<WriteResult>
{
    Guid IdempotencyKey { get; }

    Caller Caller { get; }

    // Operação e valores já interpretados que definem "a mesma requisição"; a formatação do corpo não entra.
    IReadOnlyList<string> RequestFields { get; }
}
