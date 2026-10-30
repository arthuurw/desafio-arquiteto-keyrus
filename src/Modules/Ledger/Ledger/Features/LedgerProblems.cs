using System.Diagnostics;
using Ledger.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Ledger.Features;

// Catálogo de erros do Ledger em Problem Details: o consumidor trata pelo `type`, nunca pelo texto (ADR-0020).
internal static class LedgerProblems
{
    internal static IResult Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new ProblemResult(
            StatusCodes.Status400BadRequest,
            "validation-error",
            "Dados inválidos",
            "Um ou mais campos da requisição são inválidos.",
            errors);

    internal static IResult From(LedgerError error) => error switch
    {
        // Mesmo título e detalhe para conta inexistente e conta de outro titular (ADR-0013).
        LedgerError.AccountNotFound => new ProblemResult(
            StatusCodes.Status404NotFound, "account-not-found", "Conta não encontrada", "A conta informada não existe."),
        LedgerError.EntryNotFound => new ProblemResult(
            StatusCodes.Status404NotFound, "entry-not-found", "Lançamento não encontrado", "O lançamento informado não existe nesta conta."),
        LedgerError.ConcurrencyTimeout => new ProblemResult(
            StatusCodes.Status409Conflict, "concurrency-timeout", "Conta ocupada",
            "A conta está sendo movimentada por outra requisição. Tente novamente."),
        LedgerError.EntryAlreadyReversed => new ProblemResult(
            StatusCodes.Status409Conflict, "entry-already-reversed", "Lançamento já estornado", "O lançamento já possui um estorno."),
        LedgerError.InsufficientFunds => new ProblemResult(
            StatusCodes.Status422UnprocessableEntity, "insufficient-funds", "Saldo insuficiente",
            "A operação deixaria o saldo abaixo do limite de crédito da conta."),
        LedgerError.EntryIsReversal => new ProblemResult(
            StatusCodes.Status422UnprocessableEntity, "entry-is-reversal", "Lançamento é um estorno", "Um estorno não pode ser estornado."),
        LedgerError.CreditLimitBelowBalance => new ProblemResult(
            StatusCodes.Status422UnprocessableEntity, "credit-limit-below-balance", "Limite abaixo do saldo usado",
            "O novo limite é menor que o valor que o saldo já usa abaixo de zero."),
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "Unknown ledger error."),
    };

    private sealed class ProblemResult(
        int status, string type, string title, string detail, IReadOnlyDictionary<string, string[]>? errors = null) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var problem = new ProblemDetails { Type = type, Status = status, Title = title, Detail = detail };
            if (errors is not null)
            {
                problem.Extensions["errors"] = errors;
            }

            problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
            return TypedResults.Problem(problem).ExecuteAsync(httpContext);
        }
    }
}
