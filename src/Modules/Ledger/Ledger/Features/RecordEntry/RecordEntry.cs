using BuildingBlocks.Identity;
using Ledger.Domain;
using Ledger.Features.Idempotency;
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
        [Type == EntryType.Credit ? "credit" : "debit", AccountId.ToString("D"), Amount.ToString(), Category, ExternalReference];
}

internal sealed class RecordEntryHandler(LedgerDbContext db) : ICommandHandler<RecordEntryCommand, WriteResult>
{
    public async ValueTask<WriteResult> Handle(RecordEntryCommand command, CancellationToken cancellationToken) =>
        await EntryWrites.RecordAsync(
            db,
            command.Caller,
            command.ExternalReference,
            () => db.WriteAsync(
                command.AccountId,
                command.Caller,
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
                cancellationToken),
            cancellationToken);
}

// Crédito e débito em rotas e escopos separados: quem só credita não consegue debitar (ADR-0020).
internal static class RecordEntryEndpoints
{
    internal static void Map(IEndpointRouteBuilder accounts)
    {
        accounts.MapPost("/{id}/credits", (string id, HttpRequest request, Caller caller, IMediator mediator, CancellationToken cancellationToken) =>
                RecordAsync(EntryType.Credit, id, request, caller, mediator, cancellationToken))
            .RequireScope(Scopes.LedgerCreditsWrite);

        accounts.MapPost("/{id}/debits", (string id, HttpRequest request, Caller caller, IMediator mediator, CancellationToken cancellationToken) =>
                RecordAsync(EntryType.Debit, id, request, caller, mediator, cancellationToken))
            .RequireScope(Scopes.LedgerDebitsWrite);
    }

    private static async Task<IResult> RecordAsync(
        EntryType type, string id, HttpRequest request, Caller caller, IMediator mediator, CancellationToken cancellationToken)
    {
        if (!IdempotencyKeyHeader.TryRead(request, out var idempotencyKey, out var keyProblem))
        {
            return keyProblem;
        }

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
            return LedgerProblems.Validation(input.Errors);
        }

        var result = await mediator.Send(
            new RecordEntryCommand(accountId, type, amount, category, externalReference, caller, idempotencyKey), cancellationToken);
        return result.ToHttpResult();
    }
}
