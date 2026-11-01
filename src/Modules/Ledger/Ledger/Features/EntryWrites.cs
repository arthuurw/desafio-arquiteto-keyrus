using BuildingBlocks.Identity;
using BuildingBlocks.Idempotency;
using Ledger.Domain;
using Ledger.Infrastructure.Outbox;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Features;

// Lançamento novo com referência externa única por client (ADR-0037). A conta é travada e checada antes da
// referência: conta inexistente ou de outro titular responde `404`, como em qualquer escrita (ADR-0013).
// A consulta recusa o caso comum; o índice único recusa a corrida entre contas diferentes, que ela não vê.
internal static class EntryWrites
{
    internal static async Task<WriteResult> RecordAsync(
        LedgerDbContext db,
        Guid accountId,
        Caller caller,
        string externalReference,
        Func<Account, Task<Outcome<Entry>>> decide,
        CancellationToken cancellationToken)
    {
        ExistingEntry? existing = null;
        try
        {
            var outcome = await db.WriteAsync(
                accountId,
                caller,
                async account =>
                {
                    existing = await db.FindEntryByReferenceAsync(caller.ClientId, externalReference, cancellationToken);
                    return existing is null ? await decide(account) : LedgerError.DuplicateExternalReference;
                },
                cancellationToken);
            if (outcome.IsSuccess)
            {
                await db.AppendOutboxEventAsync(outcome.Value, cancellationToken);
                return WriteResult.Created(EntryResponse.From(outcome.Value));
            }

            if (outcome.Error != LedgerError.DuplicateExternalReference)
            {
                return LedgerProblems.Refused(outcome.Error!.Value);
            }
        }
        catch (DbUpdateException exception) when (ExternalReferences.IsDuplicate(exception))
        {
            // O savepoint do EF desfez só o INSERT: a transação segue e a recusa é guardada com a chave.
            existing = await db.FindEntryByReferenceAsync(caller.ClientId, externalReference, cancellationToken);
            if (existing is null)
            {
                throw;
            }
        }

        return WriteResult.Problem(LedgerProblems.DuplicateExternalReference(
            caller.CanAccessCustomer(existing!.CustomerId) ? existing : null));
    }
}
