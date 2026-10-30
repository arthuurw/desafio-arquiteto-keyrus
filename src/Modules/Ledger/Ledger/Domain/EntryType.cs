namespace Ledger.Domain;

// O sentido da movimentação vem só do tipo; o valor é sempre positivo (ADR-0018).
internal enum EntryType
{
    Credit,
    Debit,
}

internal static class EntryTypeExtensions
{
    internal static EntryType Opposite(this EntryType type) => type == EntryType.Credit ? EntryType.Debit : EntryType.Credit;
}
