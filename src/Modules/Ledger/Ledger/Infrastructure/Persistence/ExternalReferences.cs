using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Infrastructure.Persistence;

// Lançamento que já usa uma referência externa, com o titular da conta para decidir o que revelar.
internal sealed record ExistingEntry(Guid EntryId, Guid AccountId, Guid CustomerId);

internal static class ExternalReferences
{
    private const string UniqueViolation = "23505";
    internal const string UniqueIndex = "ux_entries_client_id_external_reference";

    internal static Task<ExistingEntry?> FindEntryByReferenceAsync(
        this LedgerDbContext db, string clientId, string externalReference, CancellationToken cancellationToken) =>
        db.Entries
            .Where(entry => entry.ClientId == clientId && entry.ExternalReference == externalReference)
            .Join(db.Accounts, entry => entry.AccountId, account => account.Id,
                (entry, account) => new ExistingEntry(entry.Id, entry.AccountId, account.CustomerId))
            .SingleOrDefaultAsync(cancellationToken);

    internal static bool IsDuplicate(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: UniqueViolation, ConstraintName: UniqueIndex };
}
