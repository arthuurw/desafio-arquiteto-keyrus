using BuildingBlocks.Identity;
using Ledger.Domain;
using Ledger.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ledger.Features.RecordEntry;

internal sealed record RecordEntryCommand(
    Guid AccountId, EntryType Type, Money Amount, string Category, string ExternalReference, Caller Caller) : ICommand<Outcome<Entry>>;

internal sealed class RecordEntryHandler(LedgerDbContext db) : ICommandHandler<RecordEntryCommand, Outcome<Entry>>
{
    public async ValueTask<Outcome<Entry>> Handle(RecordEntryCommand command, CancellationToken cancellationToken) =>
        await db.WriteAsync(
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

        var outcome = await mediator.Send(new RecordEntryCommand(accountId, type, amount, category, externalReference, caller), cancellationToken);
        return outcome.IsSuccess
            ? Results.Json(EntryResponse.From(outcome.Value), statusCode: StatusCodes.Status201Created)
            : LedgerProblems.From(outcome.Error!.Value);
    }
}
