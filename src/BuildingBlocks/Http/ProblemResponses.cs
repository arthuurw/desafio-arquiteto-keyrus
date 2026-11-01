using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BuildingBlocks.Http;

// Respostas de erro em Problem Details, comuns aos módulos: o consumidor trata pelo `type`, nunca pelo texto (ADR-0020).
public static class ProblemResponses
{
    public const string ValidationErrorType = "validation-error";

    public static ProblemDetails Create(int status, string type, string title, string detail) =>
        new() { Type = type, Status = status, Title = title, Detail = detail };

    public static IResult Validation(IReadOnlyDictionary<string, string[]> errors)
    {
        var problem = Create(
            StatusCodes.Status400BadRequest,
            ValidationErrorType,
            "Dados inválidos",
            "Um ou mais campos da requisição são inválidos.");
        problem.Extensions["errors"] = errors;
        return Result(problem);
    }

    // Mesmo título e detalhe para conta inexistente e conta de outro titular, em todo módulo (ADR-0013).
    public static ProblemDetails AccountNotFound() =>
        Create(StatusCodes.Status404NotFound, "account-not-found", "Conta não encontrada", "A conta informada não existe.");

    // O `traceId` é da requisição que responde, também quando a resposta é uma repetição armazenada.
    public static IResult Result(ProblemDetails problem) => new ProblemResult(problem);

    private sealed class ProblemResult(ProblemDetails problem) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
            return TypedResults.Problem(problem).ExecuteAsync(httpContext);
        }
    }
}
