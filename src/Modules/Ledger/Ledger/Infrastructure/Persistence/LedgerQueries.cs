using BuildingBlocks.Identity;
using Ledger.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure.Persistence;

// Consultas do Ledger para Positions: leitura simples, sem lock, que não espera nenhuma escrita (ADR-0007).
internal sealed class LedgerQueries(LedgerDbContext db) : ILedgerQueries
{
    public async Task<AccountSnapshot?> AccountSnapshotAsync(Guid accountId, Caller caller, CancellationToken cancellationToken)
    {
        // No modo delegado o filtro de titular vai na consulta: conta de outro titular responde como inexistente (ADR-0013).
        var query = caller.Mode == CallerMode.Delegated
            ? db.Database.SqlQuery<SnapshotRow>(
                $"""
                SELECT id AS "AccountId", version AS "Version", balance AS "Balance", clock_timestamp() AS "Now"
                FROM ledger.accounts WHERE id = {accountId} AND customer_id = {caller.CustomerId}
                """)
            : db.Database.SqlQuery<SnapshotRow>(
                $"""
                SELECT id AS "AccountId", version AS "Version", balance AS "Balance", clock_timestamp() AS "Now"
                FROM ledger.accounts WHERE id = {accountId}
                """);
        var row = await query.SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new AccountSnapshot(row.AccountId, row.Version, row.Balance, row.Now);
    }

    // `recorded_at` não decresce com `sequence` (ADR-0005): o último até `at` é o de maior sequência.
    public async Task<LedgerEntry?> LastEntryAtAsync(Guid accountId, DateTimeOffset at, long maxSequence, CancellationToken cancellationToken)
    {
        var row = await db.Database.SqlQuery<EntryRow>(
                $"""
                SELECT id AS "Id", sequence AS "Sequence", type AS "Type", amount AS "Amount", balance_after AS "BalanceAfter",
                       category AS "Category", external_reference AS "ExternalReference", reversed_entry_id AS "ReversedEntryId",
                       recorded_at AS "RecordedAt"
                FROM ledger.entries
                WHERE account_id = {accountId} AND recorded_at <= {at.ToUniversalTime()} AND sequence <= {maxSequence}
                ORDER BY recorded_at DESC, sequence DESC
                LIMIT 1
                """)
            .SingleOrDefaultAsync(cancellationToken);
        return row?.ToEntry();
    }

    public async Task<IReadOnlyList<LedgerEntry>> EntryPageAsync(EntryPageQuery query, CancellationToken cancellationToken)
    {
        // Filtro ausente vai como nulo tipado; o banco ignora a condição.
        var from = query.From?.ToUniversalTime();
        var to = query.To?.ToUniversalTime();
        var rows = await db.Database.SqlQuery<EntryRow>(
                $"""
                SELECT id AS "Id", sequence AS "Sequence", type AS "Type", amount AS "Amount", balance_after AS "BalanceAfter",
                       category AS "Category", external_reference AS "ExternalReference", reversed_entry_id AS "ReversedEntryId",
                       recorded_at AS "RecordedAt"
                FROM ledger.entries
                WHERE account_id = {query.AccountId} AND sequence <= {query.MaxSequence}
                  AND ({query.BeforeSequence}::bigint IS NULL OR sequence < {query.BeforeSequence}::bigint)
                  AND ({from}::timestamptz IS NULL OR recorded_at >= {from}::timestamptz)
                  AND ({to}::timestamptz IS NULL OR recorded_at < {to}::timestamptz)
                ORDER BY sequence DESC
                LIMIT {query.Limit}
                """)
            .ToListAsync(cancellationToken);
        return rows.Select(row => row.ToEntry()).ToList();
    }

    private sealed record SnapshotRow(Guid AccountId, long Version, decimal Balance, DateTimeOffset Now);

    private sealed record EntryRow(
        Guid Id, long Sequence, string Type, decimal Amount, decimal BalanceAfter, string Category, string ExternalReference,
        Guid? ReversedEntryId, DateTimeOffset RecordedAt)
    {
        internal LedgerEntry ToEntry() =>
            new(Id, Sequence, Type, Amount, BalanceAfter, Category, ExternalReference, ReversedEntryId, RecordedAt);
    }
}
