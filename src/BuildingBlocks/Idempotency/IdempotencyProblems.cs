using BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BuildingBlocks.Idempotency;

// Recusas da chave de idempotência, iguais em todo módulo (ADR-0037).
public static class IdempotencyProblems
{
    public static ProblemDetails KeyMissing() =>
        ProblemResponses.Create(
            StatusCodes.Status400BadRequest, "idempotency-key-missing", "Chave de idempotência ausente",
            "Toda requisição de escrita exige o cabeçalho Idempotency-Key com um UUID gerado por operação.");

    public static ProblemDetails KeyInvalid() =>
        ProblemResponses.Create(
            StatusCodes.Status400BadRequest, "idempotency-key-invalid", "Chave de idempotência inválida",
            "O cabeçalho Idempotency-Key deve ser um UUID no formato 00000000-0000-0000-0000-000000000000.");

    public static ProblemDetails KeyReused() =>
        ProblemResponses.Create(
            StatusCodes.Status422UnprocessableEntity, "idempotency-key-reused", "Chave de idempotência reutilizada",
            "A chave já foi usada em uma requisição com conteúdo diferente. Gere uma chave nova para uma nova operação.");
}
