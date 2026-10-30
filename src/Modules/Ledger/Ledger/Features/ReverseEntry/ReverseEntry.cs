using BuildingBlocks.Identity;
using Ledger.Domain;
using Ledger.Features.Idempotency;
using Ledger.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Features.ReverseEntry;

internal sealed record ReverseEntryCommand(Guid AccountId, Guid EntryId, string ExternalReference, Caller Caller, Guid IdempotencyKey)
    : IIdempotentCommand
{
    public IReadOnlyList<string> RequestFields => ["reversal", AccountId.ToString("D"), EntryId.ToString("D"), ExternalReference];
}

// Com a conta travada, dois estornos do mesmo lançamento se enfileiram: o segundo já vê o primeiro.
internal sealed class ReverseEntryHandler(LedgerDbContext db) : ICommandHandler<ReverseEntryCommand, WriteResult>
{
    public async ValueTask<WriteResult> Handle(ReverseEntryCommand command, CancellationToken cancellationToken) =>
        await EntryWrites.RecordAsync(
            db,
            command.Caller,
            command.ExternalReference,
            () => db.WriteAsync(
                command.AccountId,
                command.Caller,
                async account =>
                {
                    // Lançamento de outra conta responde como inexistente nesta.
                    var original = await db.Entries.AsNoTracking()
                        .SingleOrDefaultAsync(entry => entry.Id == command.EntryId && entry.AccountId == account.Id, cancellationToken);
                    if (original is null)
                    {
                        return LedgerError.EntryNotFound;
                    }

                    var alreadyReversed = await db.Entries.AnyAsync(entry => entry.ReversedEntryId == original.Id, cancellationToken);
                    var recordedAt = await db.RecordingTimeAsync(account.Id, cancellationToken);
                    var outcome = account.Reverse(original, alreadyReversed, command.ExternalReference, command.Caller.ClientId, recordedAt);
                    if (outcome.IsSuccess)
                    {
                        db.Entries.Add(outcome.Value);
                    }

                    return outcome;
                },
                cancellationToken),
            cancellationToken);
}

internal static class ReverseEntryEndpoint
{
    internal static void Map(IEndpointRouteBuilder accounts) =>
        accounts.MapPost(
                "/{id}/entries/{entryId}/reversal",
                async (string id, string entryId, HttpRequest request, Caller caller, IMediator mediator, CancellationToken cancellationToken) =>
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
                    var externalReference = input.ExternalReference("externalReference");
                    if (!input.IsValid || externalReference is null)
                    {
                        return LedgerProblems.Validation(input.Errors);
                    }

                    // Id fora do formato não existe; a conta ainda é checada primeiro pelo handler.
                    var reversedEntryId = Guid.TryParse(entryId, out var parsed) ? parsed : Guid.Empty;
                    var result = await mediator.Send(
                        new ReverseEntryCommand(accountId, reversedEntryId, externalReference, caller, idempotencyKey), cancellationToken);
                    return result.ToHttpResult();
                })
            .RequireScope(Scopes.LedgerEntriesReverse);
}
