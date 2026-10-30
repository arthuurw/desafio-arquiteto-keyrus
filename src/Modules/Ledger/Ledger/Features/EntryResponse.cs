using Ledger.Domain;

namespace Ledger.Features;

// Contrato do lançamento: dinheiro como string com 2 casas (ADR-0018), instante em UTC terminado em `Z`.
internal sealed record EntryResponse(
    Guid Id,
    Guid AccountId,
    string Type,
    string Amount,
    string Category,
    string ExternalReference,
    Guid? ReversedEntryId,
    string BalanceAfter,
    long Sequence,
    long Version,
    DateTime RecordedAt)
{
    // A versão da conta depois do lançamento é a sequência dele.
    internal static EntryResponse From(Entry entry) => new(
        entry.Id,
        entry.AccountId,
        entry.Type == EntryType.Credit ? "credit" : "debit",
        entry.Amount.ToString(),
        entry.Category,
        entry.ExternalReference,
        entry.ReversedEntryId,
        entry.BalanceAfter.ToString(),
        entry.Sequence,
        entry.Sequence,
        entry.RecordedAt.UtcDateTime);
}
