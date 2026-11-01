using BuildingBlocks.Http;
using BuildingBlocks.Idempotency;
using Ledger.Domain;
using Ledger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Ledger.Features;

// Catálogo de erros do Ledger em Problem Details: o consumidor trata pelo `type`, nunca pelo texto (ADR-0042).
internal static class LedgerProblems
{
    internal static IResult From(LedgerError error) => ProblemResponses.Result(Describe(error));

    // Recusa de um comando idempotente; lock esgotado é a única que repetir pode mudar, e não é guardada (ADR-0037).
    internal static WriteResult Refused(LedgerError error) =>
        WriteResult.Problem(Describe(error)) with { Transient = error == LedgerError.ConcurrencyTimeout };

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
        LedgerError.AccountNotFound => ProblemResponses.AccountNotFound(),
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
        // `404`, não `403`, para o cliente que o delegado não representa: não revela se ele existe (ADR-0013).
        LedgerError.CustomerNotFound => Problem(
            StatusCodes.Status404NotFound, "customer-not-found", "Cliente não encontrado", "O cliente informado não existe."),
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "Unknown ledger error."),
    };

    private static ProblemDetails Problem(int status, string type, string title, string detail) =>
        ProblemResponses.Create(status, type, title, detail);
}
