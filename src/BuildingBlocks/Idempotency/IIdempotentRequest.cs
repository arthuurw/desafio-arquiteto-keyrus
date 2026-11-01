using BuildingBlocks.Identity;

namespace BuildingBlocks.Idempotency;

// O que o fluxo de idempotência lê de um comando de escrita com chave (ADR-0037). Cada módulo estende esta interface
// com a sua, e o behavior do módulo se restringe a ela: o behavior de um módulo nunca envolve comando de outro.
public interface IIdempotentRequest
{
    Guid IdempotencyKey { get; }

    Caller Caller { get; }

    // Operação e valores já interpretados que definem "a mesma requisição"; a formatação do corpo não entra.
    IReadOnlyList<string> RequestFields { get; }
}
