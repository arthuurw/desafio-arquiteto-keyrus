using System.ComponentModel;
using BuildingBlocks.Identity;
using Ledger.Domain;
using Ledger.Infrastructure.Idempotency;
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
    public IReadOnlyList<string> RequestFields => [IdempotentOperation.Reversal, AccountId.ToString("D"), EntryId.ToString("D"), ExternalReference];
}

// Com a conta travada, dois estornos do mesmo lançamento se enfileiram: o segundo já vê o primeiro.
internal sealed class ReverseEntryHandler(LedgerDbContext db) : ICommandHandler<ReverseEntryCommand, WriteResult>
{
    public async ValueTask<WriteResult> Handle(ReverseEntryCommand command, CancellationToken cancellationToken) =>
        await EntryWrites.RecordAsync(
            db,
            command.AccountId,
            command.Caller,
            command.ExternalReference,
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

                    var input = await LedgerInput.ReadAsync(request, cancellationToken);
                    var externalReference = input.ExternalReference("externalReference");
                    if (!input.IsValid || externalReference is null)
                    {
                        return LedgerProblems.Validation(input.Errors);
                    }

                    // Ids fora do formato não existem: vão como Guid.Empty, e o `404` é guardado com a chave.
                    var accountId = Guid.TryParse(id, out var parsedAccount) ? parsedAccount : Guid.Empty;
                    var reversedEntryId = Guid.TryParse(entryId, out var parsedEntry) ? parsedEntry : Guid.Empty;
                    var result = await mediator.Send(
                        new ReverseEntryCommand(accountId, reversedEntryId, externalReference, caller, idempotencyKey), cancellationToken);
                    return result.ToHttpResult();
                })
            .RequireScope(Scopes.LedgerEntriesReverse)
            .Accepts<ReversalRequest>("application/json")
            .Produces<EntryResponse>(StatusCodes.Status201Created)
            .Documented(
                "Estorna um lançamento",
                "Registra o lançamento oposto ao informado, com a categoria REVERSAL. Um estorno por lançamento, "
                    + "e estorno não é estornado. O estorno de um crédito respeita o limite; o de um débito, o saldo máximo.",
                idempotencyKey: true,
                LedgerError.AccountNotFound,
                LedgerError.EntryNotFound,
                LedgerError.EntryAlreadyReversed,
                LedgerError.DuplicateExternalReference,
                LedgerError.EntryIsReversal,
                LedgerError.InsufficientFunds,
                LedgerError.BalanceLimitExceeded);
}

// Corpo do estorno, para o contrato publicado.
internal sealed record ReversalRequest(
    [property: Description("Identificador do estorno no sistema de origem, único por client.")] string ExternalReference);
