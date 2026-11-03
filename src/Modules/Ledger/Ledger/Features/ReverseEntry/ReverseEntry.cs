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
                async (string id, string entryId, HttpRequest request, ClaimsPrincipal user, ILedgerCommands commands, CancellationToken cancellationToken) =>
                {
                    if (!IdempotencyKeyHeader.TryRead(request, out var idempotencyKey, out var keyProblem))
                    {
                        return keyProblem;
                    }

                    // Ids fora do formato não existem: o `404` sai antes da idempotência e não consome a chave.
                    if (!Guid.TryParse(id, out var accountId))
                    {
                        return LedgerProblems.From(LedgerError.AccountNotFound);
                    }

                    if (!Guid.TryParse(entryId, out var reversedEntryId))
                    {
                        return LedgerProblems.From(LedgerError.EntryNotFound);
                    }

                    var input = await LedgerInput.ReadAsync(request, cancellationToken);
                    var externalReference = input.ExternalReference("externalReference");
                    if (!input.IsValid || externalReference is null)
                    {
                        return ProblemResponses.Validation(input.Errors);
                    }

                    var result = await commands.Send(
                        new ReverseEntryCommand(accountId, reversedEntryId, externalReference, user.ResolvedCaller(), idempotencyKey), cancellationToken);
                    return result.ToHttpResult();
                })
            .RequireScope(Scopes.LedgerEntriesReverse)
            .Audited(LedgerAudit.Trail, LedgerAudit.ReverseEntry, LedgerAudit.Entry, "entryId")
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
