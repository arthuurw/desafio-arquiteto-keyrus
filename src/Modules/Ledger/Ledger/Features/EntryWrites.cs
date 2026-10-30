using BuildingBlocks.Identity;
using Ledger.Domain;
using Ledger.Features.Idempotency;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Features;

// Lançamento novo com referência externa única por client (ADR-0014): a consulta recusa o caso comum e o
// índice único recusa a corrida entre contas diferentes, que a consulta não vê.
internal static class EntryWrites
{
    internal static async Task<WriteResult> RecordAsync(
        LedgerDbContext db, Caller caller, string externalReference, Func<Task<Outcome<Entry>>> write, CancellationToken cancellationToken)
    {
        var existing = await db.FindEntryByReferenceAsync(caller.ClientId, externalReference, cancellationToken);
        if (existing is null)
        {
            try
            {
                var outcome = await write();
                return outcome.IsSuccess
                    ? WriteResult.Created(EntryResponse.From(outcome.Value))
                    : WriteResult.Rejected(outcome.Error!.Value);
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
        }

        // Os ids só vão para quem pode ver a conta: no modo delegado, só o titular dela (ADR-0013).
        var visible = caller.Mode == CallerMode.Service || existing.CustomerId == caller.CustomerId;
        return WriteResult.Problem(LedgerProblems.DuplicateExternalReference(
            visible ? existing.EntryId : null, visible ? existing.AccountId : null));
    }
}
