namespace Ledger.Contracts;

// Evento de integração de um lançamento confirmado, gravado no outbox na transação dele (ADR-0006, ADR-0035).
// `Type` é `credit` ou `debit`; `ReversedEntryId` só no estorno.
public sealed record EntryRecorded(
    Guid EntryId,
    Guid AccountId,
    long Sequence,
    string Type,
    decimal Amount,
    decimal BalanceAfter,
    string Category,
    string ExternalReference,
    Guid? ReversedEntryId,
    DateTimeOffset RecordedAt)
{
    // Muda de versão quando o payload muda de forma incompatível.
    public const string EventType = "EntryRecorded.v1";
}
