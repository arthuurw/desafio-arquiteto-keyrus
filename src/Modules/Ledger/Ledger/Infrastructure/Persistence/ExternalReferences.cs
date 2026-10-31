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
        db.Database
            .SqlQuery<ExistingEntry>(
                $"""
                SELECT e.id AS "EntryId", e.account_id AS "AccountId", a.customer_id AS "CustomerId"
                FROM ledger.entries e JOIN ledger.accounts a ON a.id = e.account_id
                WHERE e.client_id = {clientId} AND e.external_reference = {externalReference}
                """)
            .SingleOrDefaultAsync(cancellationToken);

    internal static bool IsDuplicate(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: UniqueViolation, ConstraintName: UniqueIndex };
}
