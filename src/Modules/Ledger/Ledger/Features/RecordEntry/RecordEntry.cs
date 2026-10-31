using System.ComponentModel;
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

// Crédito e débito em rotas e escopos separados: quem só credita não consegue debitar (ADR-0020).
internal static class RecordEntryEndpoints
{
    internal static void Map(IEndpointRouteBuilder accounts)
    {
        accounts.MapPost("/{id}/credits", (string id, HttpRequest request, Caller caller, IMediator mediator, CancellationToken cancellationToken) =>
                RecordAsync(EntryType.Credit, id, request, caller, mediator, cancellationToken))
            .RequireScope(Scopes.LedgerCreditsWrite)
            .Accepts<EntryRequest>("application/json")
            .Produces<EntryResponse>(StatusCodes.Status201Created)
            .Documented(
                "Registra um crédito",
                "Credita o valor na conta. O crédito não é recusado por limite, só quando levaria o saldo acima do máximo. "
                    + "A referência externa é única por client.",
                idempotencyKey: true,
                LedgerError.AccountNotFound,
                LedgerError.DuplicateExternalReference,
                LedgerError.BalanceLimitExceeded);

        accounts.MapPost("/{id}/debits", (string id, HttpRequest request, Caller caller, IMediator mediator, CancellationToken cancellationToken) =>
                RecordAsync(EntryType.Debit, id, request, caller, mediator, cancellationToken))
            .RequireScope(Scopes.LedgerDebitsWrite)
            .Accepts<EntryRequest>("application/json")
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
        EntryType type, string id, HttpRequest request, Caller caller, IMediator mediator, CancellationToken cancellationToken)
    {
        if (!IdempotencyKeyHeader.TryRead(request, out var idempotencyKey, out var keyProblem))
        {
            return keyProblem;
        }

        var input = await LedgerInput.ReadAsync(request, cancellationToken);
        var amount = input.Amount("amount");
        var category = input.Category("category", type);
        var externalReference = input.ExternalReference("externalReference");
        if (!input.IsValid || amount is null || category is null || externalReference is null)
        {
            return LedgerProblems.Validation(input.Errors);
        }

        // Id fora do formato não existe: vai como Guid.Empty e o `404` é guardado com a chave, como o de uma conta inexistente.
        var accountId = Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty;
        var result = await mediator.Send(
            new RecordEntryCommand(accountId, type, amount, category, externalReference, caller, idempotencyKey), cancellationToken);
        return result.ToHttpResult();
    }
}

// Corpo do crédito e do débito, para o contrato publicado.
internal sealed record EntryRequest(
    [property: Description("Valor em BRL, string com até 2 casas, maior que zero. Ex.: \"150.75\".")] string Amount,
    [property: Description("Categoria do catálogo, compatível com o tipo do lançamento. Ex.: PIX_IN, PIX_OUT.")] string Category,
    [property: Description("Identificador do lançamento no sistema de origem, único por client.")] string ExternalReference);
