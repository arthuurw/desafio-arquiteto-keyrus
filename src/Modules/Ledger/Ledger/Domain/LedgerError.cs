namespace Ledger.Domain;

// Recusas de negócio do Ledger; cada uma tem um `type` estável na Api (ADR-0020).
internal enum LedgerError
{
    AccountNotFound,
    EntryNotFound,
    ConcurrencyTimeout,
    EntryAlreadyReversed,
    InsufficientFunds,
    EntryIsReversal,
    CreditLimitBelowBalance,
}
