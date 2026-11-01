using Ledger.Contracts;

namespace Positions.Features;

internal sealed record BalanceResponse(Guid AccountId, string Balance, DateTime At, long Version);

internal sealed record StatementItem(
    Guid Id,
    string Type,
    string Amount,
    string Category,
    string ExternalReference,
    Guid? ReversedEntryId,
    string BalanceAfter,
    long Sequence,
    DateTime RecordedAt)
{
    internal static StatementItem From(LedgerEntry entry) => new(
        entry.Id,
        entry.Type,
        PositionsFormat.Money(entry.Amount),
        entry.Category,
        entry.ExternalReference,
        entry.ReversedEntryId,
        PositionsFormat.Money(entry.BalanceAfter),
        entry.Sequence,
        PositionsFormat.Utc(entry.RecordedAt));
}

// Página do extrato; `NextCursor` nulo na última página.
internal sealed record Statement(Guid AccountId, IReadOnlyList<StatementItem> Items, string? NextCursor);
