using System.Globalization;
using Ledger.Contracts;

namespace Positions.Features;

// Dinheiro como string com 2 casas (ADR-0018); instante em UTC terminado em `Z`.
internal static class PositionsFormat
{
    internal static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    internal static DateTime Utc(DateTimeOffset instant) => instant.UtcDateTime;
}

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

// `NextCursor` nulo na última página.
internal sealed record StatementResponse(Guid AccountId, IReadOnlyList<StatementItem> Items, string? NextCursor);
