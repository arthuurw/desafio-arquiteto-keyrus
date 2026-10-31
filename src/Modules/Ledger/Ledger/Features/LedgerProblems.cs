using System.Diagnostics;
using Ledger.Domain;
using Ledger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Ledger.Features;

// Catálogo de erros do Ledger em Problem Details: o consumidor trata pelo `type`, nunca pelo texto (ADR-0020).
internal static class LedgerProblems
{
    internal static IResult Validation(IReadOnlyDictionary<string, string[]> errors)
    {
        var problem = Problem(
            StatusCodes.Status400BadRequest,
            "validation-error",
            "Dados inválidos",
            "Um ou mais campos da requisição são inválidos.");
        problem.Extensions["errors"] = errors;
        return Result(problem);
    }

    internal static IResult From(LedgerError error) => Result(Describe(error));

    internal static IResult IdempotencyKeyMissing() =>
        Result(Problem(
            StatusCodes.Status400BadRequest, "idempotency-key-missing", "Chave de idempotência ausente",
            "Toda requisição de escrita exige o cabeçalho Idempotency-Key com um UUID gerado por operação."));

    internal static IResult IdempotencyKeyInvalid() =>
        Result(Problem(
            StatusCodes.Status400BadRequest, "idempotency-key-invalid", "Chave de idempotência inválida",
            "O cabeçalho Idempotency-Key deve ser um UUID no formato 00000000-0000-0000-0000-000000000000."));

    internal static ProblemDetails IdempotencyKeyReused() =>
        Problem(
            StatusCodes.Status422UnprocessableEntity, "idempotency-key-reused", "Chave de idempotência reutilizada",
            "A chave já foi usada em uma requisição com conteúdo diferente. Gere uma chave nova para uma nova operação.");

    // Os ids só vão quando o chamador pode ver a conta do lançamento existente (ADR-0013).
    internal static ProblemDetails DuplicateExternalReference(ExistingEntry? visible)
    {
        var problem = Describe(LedgerError.DuplicateExternalReference);
        if (visible is not null)
        {
            problem.Extensions["entryId"] = visible.EntryId;
            problem.Extensions["accountId"] = visible.AccountId;
        }

        return problem;
    }

    internal static ProblemDetails Describe(LedgerError error) => error switch
    {
        // Mesmo título e detalhe para conta inexistente e conta de outro titular (ADR-0013).
        LedgerError.AccountNotFound => Problem(
            StatusCodes.Status404NotFound, "account-not-found", "Conta não encontrada", "A conta informada não existe."),
        LedgerError.EntryNotFound => Problem(
            StatusCodes.Status404NotFound, "entry-not-found", "Lançamento não encontrado", "O lançamento informado não existe nesta conta."),
        LedgerError.ConcurrencyTimeout => Problem(
            StatusCodes.Status409Conflict, "concurrency-timeout", "Conta ocupada",
            "A conta está sendo movimentada por outra requisição. Tente novamente."),
        LedgerError.EntryAlreadyReversed => Problem(
            StatusCodes.Status409Conflict, "entry-already-reversed", "Lançamento já estornado", "O lançamento já possui um estorno."),
        LedgerError.InsufficientFunds => Problem(
            StatusCodes.Status422UnprocessableEntity, "insufficient-funds", "Saldo insuficiente",
            "A operação deixaria o saldo abaixo do limite de crédito da conta."),
        LedgerError.EntryIsReversal => Problem(
            StatusCodes.Status422UnprocessableEntity, "entry-is-reversal", "Lançamento é um estorno", "Um estorno não pode ser estornado."),
        LedgerError.CreditLimitBelowBalance => Problem(
            StatusCodes.Status422UnprocessableEntity, "credit-limit-below-balance", "Limite abaixo do saldo usado",
            "O novo limite é menor que o valor que o saldo já usa abaixo de zero."),
        LedgerError.DuplicateExternalReference => Problem(
            StatusCodes.Status409Conflict, "duplicate-external-reference", "Referência externa já registrada",
            "Já existe um lançamento com esta referência externa para este client."),
        LedgerError.BalanceLimitExceeded => Problem(
            StatusCodes.Status422UnprocessableEntity, "balance-limit-exceeded", "Saldo acima do máximo",
            "A operação deixaria o saldo acima do máximo que a conta comporta."),
        // Mesma resposta para cliente de outro titular e, a partir do cadastro, cliente inexistente (ADR-0013).
        LedgerError.CustomerNotFound => Problem(
            StatusCodes.Status404NotFound, "customer-not-found", "Cliente não encontrado", "O cliente informado não existe."),
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "Unknown ledger error."),
    };

    // O `traceId` é da requisição que responde, também quando a resposta é uma repetição armazenada.
    internal static IResult Result(ProblemDetails problem) => new ProblemResult(problem);

    private static ProblemDetails Problem(int status, string type, string title, string detail) =>
        new() { Type = type, Status = status, Title = title, Detail = detail };

    private sealed class ProblemResult(ProblemDetails problem) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
            return TypedResults.Problem(problem).ExecuteAsync(httpContext);
        }
    }
}
