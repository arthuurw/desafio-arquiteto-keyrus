namespace Ledger.Domain;

// Agregado de consistência: saldo, limite e versão mudam só por aqui (ADR-0001, ADR-0003).
internal sealed class Account
{
    // Usado pelo EF Core ao materializar.
    private Account()
    {
        Balance = Money.Zero;
        CreditLimit = Money.Zero;
    }

    internal Guid Id { get; private set; }

    internal Guid CustomerId { get; private set; }

    internal Money Balance { get; private set; }

    internal Money CreditLimit { get; private set; }

    // Conta lançamentos: a sequência do próximo é `Version + 1`.
    internal long Version { get; private set; }

    internal DateTimeOffset? LastRecordedAt { get; private set; }

    internal DateTimeOffset CreatedAt { get; private set; }

    internal static Account Open(Guid customerId, DateTimeOffset createdAt) => new()
    {
        Id = Guid.CreateVersion7(),
        CustomerId = customerId,
        CreatedAt = createdAt,
    };

    internal bool BelongsTo(Guid customerId) => CustomerId == customerId;

    // Crédito nunca é recusado por saldo (ADR-0001).
    internal Outcome<Entry> Credit(Money amount, string category, string externalReference, string clientId, DateTimeOffset recordedAt)
    {
        EnsureCategory(EntryType.Credit, category);
        return Record(EntryType.Credit, amount, category, externalReference, clientId, reversedEntryId: null, recordedAt);
    }

    internal Outcome<Entry> Debit(Money amount, string category, string externalReference, string clientId, DateTimeOffset recordedAt)
    {
        EnsureCategory(EntryType.Debit, category);
        return Record(EntryType.Debit, amount, category, externalReference, clientId, reversedEntryId: null, recordedAt);
    }

    // Um estorno por lançamento; o estorno de crédito é um débito e respeita o limite.
    internal Outcome<Entry> Reverse(Entry original, bool alreadyReversed, string externalReference, string clientId, DateTimeOffset recordedAt)
    {
        if (original.AccountId != Id)
        {
            throw new ArgumentException("The entry belongs to another account.", nameof(original));
        }

        if (original.IsReversal)
        {
            return LedgerError.EntryIsReversal;
        }

        if (alreadyReversed)
        {
            return LedgerError.EntryAlreadyReversed;
        }

        return Record(original.Type.Opposite(), original.Amount, Categories.Reversal, externalReference, clientId, original.Id, recordedAt);
    }

    // Não gera lançamento nem muda a versão; o limite novo precisa cobrir o saldo já negativo.
    internal LedgerError? ChangeCreditLimit(Money creditLimit)
    {
        if (creditLimit.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(creditLimit), "The credit limit cannot be negative.");
        }

        if (Balance < -creditLimit)
        {
            return LedgerError.CreditLimitBelowBalance;
        }

        CreditLimit = creditLimit;
        return null;
    }

    private Outcome<Entry> Record(
        EntryType type, Money amount, string category, string externalReference, string clientId, Guid? reversedEntryId, DateTimeOffset recordedAt)
    {
        if (!amount.IsPositive)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "An entry amount must be greater than zero.");
        }

        // A consulta de saldo em um momento depende de `recorded_at` não decrescente (ADR-0005).
        if (recordedAt < LastRecordedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(recordedAt), "An entry cannot be recorded before the previous one.");
        }

        var balanceAfter = type == EntryType.Credit ? Balance + amount : Balance - amount;
        if (type == EntryType.Debit && balanceAfter < -CreditLimit)
        {
            return LedgerError.InsufficientFunds;
        }

        Balance = balanceAfter;
        Version++;
        LastRecordedAt = recordedAt;
        return Outcome<Entry>.Success(new Entry(
            Id, Version, type, amount, balanceAfter, category, externalReference, clientId, reversedEntryId, recordedAt));
    }

    private static void EnsureCategory(EntryType type, string category)
    {
        if (!Categories.Accepts(type, category))
        {
            throw new ArgumentException($"Category '{category}' is not valid for a {type} entry.", nameof(category));
        }
    }
}
