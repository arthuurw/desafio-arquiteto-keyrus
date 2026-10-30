namespace Ledger.Domain;

// Lançamento imutável: nasce completo e nunca muda (ADR-0003).
internal sealed class Entry
{
    internal Entry(
        Guid accountId,
        long sequence,
        EntryType type,
        Money amount,
        Money balanceAfter,
        string category,
        string externalReference,
        string clientId,
        Guid? reversedEntryId,
        DateTimeOffset recordedAt)
    {
        Id = Guid.CreateVersion7();
        AccountId = accountId;
        Sequence = sequence;
        Type = type;
        Amount = amount;
        BalanceAfter = balanceAfter;
        Category = category;
        ExternalReference = externalReference;
        ClientId = clientId;
        ReversedEntryId = reversedEntryId;
        RecordedAt = recordedAt;
    }

    // Usado pelo EF Core, que preenche todas as propriedades ao materializar.
    private Entry()
    {
        Amount = null!;
        BalanceAfter = null!;
        Category = null!;
        ExternalReference = null!;
        ClientId = null!;
    }

    internal Guid Id { get; private set; }

    internal Guid AccountId { get; private set; }

    internal long Sequence { get; private set; }

    internal EntryType Type { get; private set; }

    internal Money Amount { get; private set; }

    internal Money BalanceAfter { get; private set; }

    internal string Category { get; private set; }

    internal string ExternalReference { get; private set; }

    internal string ClientId { get; private set; }

    // Preenchido só no estorno: o lançamento que ele anula.
    internal Guid? ReversedEntryId { get; private set; }

    internal DateTimeOffset RecordedAt { get; private set; }

    internal bool IsReversal => ReversedEntryId is not null;
}
