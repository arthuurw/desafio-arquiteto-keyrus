using BuildingBlocks.Http;
using BuildingBlocks.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Customers.Features;

// Catálogo de erros de Customers em Problem Details: o consumidor trata pelo `type`, nunca pelo texto (ADR-0042).
internal static class CustomersProblems
{
    internal const string RetryAfterSeconds = "5";

    // Mesmo título e detalhe para id inexistente e cliente que o delegado não representa (ADR-0013).
    internal static ProblemDetails CustomerNotFound() =>
        ProblemResponses.Create(StatusCodes.Status404NotFound, "customer-not-found", "Cliente não encontrado", "O cliente informado não existe.");

    // Sem o id do cliente existente: a recusa não revela de quem é o CPF.
    internal static ProblemDetails CpfAlreadyRegistered() =>
        ProblemResponses.Create(
            StatusCodes.Status409Conflict, "cpf-already-registered", "CPF já cadastrado", "Já existe um cliente cadastrado com este CPF.");

    internal static ProblemDetails PersonalDataUnavailable() =>
        ProblemResponses.Create(
            StatusCodes.Status503ServiceUnavailable, "personal-data-unavailable", "Dados pessoais indisponíveis",
            "O cofre de chaves não respondeu. Tente novamente em alguns segundos.");

    // Transitória: o behavior desfaz a transação, a chave não é consumida e a repetição executa de novo (ADR-0037).
    internal static WriteResult Unavailable() =>
        WriteResult.Problem(PersonalDataUnavailable()) with
        {
            Transient = true,
            Headers = new Dictionary<string, string> { ["Retry-After"] = RetryAfterSeconds },
        };

    internal static IResult UnavailableResult() => Unavailable().ToHttpResult();
}
