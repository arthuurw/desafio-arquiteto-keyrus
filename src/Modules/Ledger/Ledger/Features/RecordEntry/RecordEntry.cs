using System.ComponentModel;
using System.Security.Claims;
using BuildingBlocks.Audit;
using BuildingBlocks.Http;
using BuildingBlocks.Idempotency;
using BuildingBlocks.Identity;
using Ledger.Domain;
using Ledger.Infrastructure.Idempotency;
using Ledger.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ledger.Features.RecordEntry;

internal sealed record RecordEntryCommand(
    Guid AccountId, EntryType Type, Money Amount, string Category, string ExternalReference, Caller Caller, Guid IdempotencyKey)
    : IIdempotentCommand
{
    public IReadOnlyList<string> RequestFields =>
        [
            Type == EntryType.Credit ? IdempotentOperation.Credit : IdempotentOperation.Debit,
            AccountId.ToString("D"),
            Amount.ToString(),
            Category,
            ExternalReference,
        ];
}

internal sealed class RecordEntryHandler(LedgerDbContext db) : ICommandHandler<RecordEntryCommand, WriteResult>
{
    public async ValueTask<WriteResult> Handle(RecordEntryCommand command, CancellationToken cancellationToken) =>
        await EntryWrites.RecordAsync(
            db,
            command.AccountId,
            command.Caller,
            command.ExternalReference,
            async account =>
            {
                var recordedAt = await db.RecordingTimeAsync(account.Id, cancellationToken);
                var outcome = command.Type == EntryType.Credit
                    ? account.Credit(command.Amount, command.Category, command.ExternalReference, command.Caller.ClientId, recordedAt)
                    : account.Debit(command.Amount, command.Category, command.ExternalReference, command.Caller.ClientId, recordedAt);
                if (outcome.IsSuccess)
                {
                    db.Entries.Add(outcome.Value);
                }

                return outcome;
            },
            cancellationToken);
}

// Crédito e débito em rotas e escopos separados: quem só credita não consegue debitar (ADR-0042).
internal static class RecordEntryEndpoints
{
    internal static void Map(IEndpointRouteBuilder accounts)
    {
        accounts.MapPost("/{id}/credits", (string id, HttpRequest request, ClaimsPrincipal user, ILedgerCommands commands, CancellationToken cancellationToken) =>
                RecordAsync(EntryType.Credit, id, request, user.ResolvedCaller(), commands, cancellationToken))
            .RequireScope(Scopes.LedgerCreditsWrite)
            .Audited(LedgerAudit.Trail, LedgerAudit.Credit, LedgerAudit.Account, "id")
            .Accepts<CreditRequest>("application/json")
            .Produces<EntryResponse>(StatusCodes.Status201Created)
            .Documented(
                "Registra um crédito",
                "Credita o valor na conta. O crédito não é recusado por limite, só quando levaria o saldo acima do máximo. "
                    + "A referência externa é única por client.",
                idempotencyKey: true,
                LedgerError.AccountNotFound,
                LedgerError.DuplicateExternalReference,
                LedgerError.BalanceLimitExceeded);

        accounts.MapPost("/{id}/debits", (string id, HttpRequest request, ClaimsPrincipal user, ILedgerCommands commands, CancellationToken cancellationToken) =>
                RecordAsync(EntryType.Debit, id, request, user.ResolvedCaller(), commands, cancellationToken))
            .RequireScope(Scopes.LedgerDebitsWrite)
            .Audited(LedgerAudit.Trail, LedgerAudit.Debit, LedgerAudit.Account, "id")
            .Accepts<DebitRequest>("application/json")
            .Produces<EntryResponse>(StatusCodes.Status201Created)
            .Documented(
                "Registra um débito",
                "Debita o valor da conta. O saldo pode ficar negativo até o limite de crédito, nunca abaixo dele. "
                    + "A referência externa é única por client.",
                idempotencyKey: true,
                LedgerError.AccountNotFound,
                LedgerError.DuplicateExternalReference,
                LedgerError.InsufficientFunds);
    }

    private static async Task<IResult> RecordAsync(
        EntryType type, string id, HttpRequest request, Caller caller, ILedgerCommands commands, CancellationToken cancellationToken)
    {
        if (!IdempotencyKeyHeader.TryRead(request, out var idempotencyKey, out var keyProblem))
        {
            return keyProblem;
        }

        // Id fora do formato não é uma conta: o `404` sai antes da idempotência e não consome a chave, que segue livre
        // para outra rota. O de uma conta inexistente é guardado com ela, porque a conta pode passar a existir.
        if (!Guid.TryParse(id, out var accountId))
        {
            return LedgerProblems.From(LedgerError.AccountNotFound);
        }

        var input = await LedgerInput.ReadAsync(request, cancellationToken);
        var amount = input.Amount("amount");
        var category = input.Category("category", type);
        var externalReference = input.ExternalReference("externalReference");
        if (!input.IsValid || amount is null || category is null || externalReference is null)
        {
            return ProblemResponses.Validation(input.Errors);
        }

        var result = await commands.Send(
            new RecordEntryCommand(accountId, type, amount, category, externalReference, caller, idempotencyKey), cancellationToken);
        return result.ToHttpResult();
    }
}

// Corpo do crédito, para o contrato publicado; o catálogo de `category` entra pelo `LedgerOpenApi`.
internal sealed record CreditRequest(
    [property: Description("Valor em BRL, string com até 2 casas, maior que zero. Ex.: \"150.75\".")] string Amount,
    [property: Description("Categoria do catálogo de crédito. Ex.: PIX_IN.")] string Category,
    [property: Description("Identificador do lançamento no sistema de origem, único por client.")] string ExternalReference);

// Corpo do débito, para o contrato publicado; o catálogo de `category` entra pelo `LedgerOpenApi`.
internal sealed record DebitRequest(
    [property: Description("Valor em BRL, string com até 2 casas, maior que zero. Ex.: \"150.75\".")] string Amount,
    [property: Description("Categoria do catálogo de débito. Ex.: PIX_OUT.")] string Category,
    [property: Description("Identificador do lançamento no sistema de origem, único por client.")] string ExternalReference);
