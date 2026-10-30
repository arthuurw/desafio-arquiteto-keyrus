using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Ledger.Domain;

namespace Ledger.UnitTests;

public sealed class AccountProperties
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    // Cada sequência parte de uma conta nova; o oráculo é a regra do ADR-0001 escrita de forma direta.
    [Property(MaxTest = 1000, Arbitrary = [typeof(Operations)])]
    public bool RandomOperationsKeepInvariants(Operation[] operations)
    {
        var account = Account.Open(Guid.NewGuid(), Start);
        var entries = new List<Entry>();
        var reversed = new HashSet<Guid>();

        for (var step = 0; step < operations.Length; step++)
        {
            var balanceBefore = account.Balance;
            var limitBefore = account.CreditLimit;
            var versionBefore = account.Version;
            var recordedAt = Start.AddSeconds(step);

            var (refusal, expectedRefusal) = operations[step] switch
            {
                Operation.Credit credit => Apply(account.Credit(Money.Of(credit.Amount), "PIX_IN", "REF", "client", recordedAt), entries),
                Operation.Debit debit => Debit(account, entries, debit.Amount, recordedAt),
                // Débito que leva o saldo a exatamente `-limit`, um centavo antes ou um depois.
                Operation.DebitToLimit near => Debit(
                    account, entries, Math.Max(0.01m, balanceBefore.Amount + limitBefore.Amount + near.OffsetCents / 100m), recordedAt),
                Operation.Reverse reverse when entries.Count > 0 => ApplyReversal(account, entries, reversed, reverse.Index, recordedAt),
                Operation.Reverse => (null, null),
                Operation.ChangeLimit change => ChangeLimit(account, change.Limit),
                // Limite novo igual ao que o saldo já usa abaixo de zero, um centavo a menos ou a mais.
                Operation.ChangeLimitToBalance near => ChangeLimit(account, Math.Max(0m, -balanceBefore.Amount + near.OffsetCents / 100m)),
                _ => throw new InvalidOperationException("Unknown operation."),
            };

            refusal.ShouldBe(expectedRefusal, $"step {step}: {operations[step]}");
            if (refusal is not null)
            {
                account.Balance.ShouldBe(balanceBefore);
                account.Version.ShouldBe(versionBefore);
                account.CreditLimit.ShouldBe(limitBefore);
            }

            (account.Balance >= -account.CreditLimit).ShouldBeTrue($"step {step}: balance {account.Balance} below -{account.CreditLimit}");
            ShouldKeepChain(account, entries);
        }

        return true;
    }

    private static (LedgerError? Refusal, LedgerError? Expected) Debit(Account account, List<Entry> entries, decimal amount, DateTimeOffset recordedAt)
    {
        var expected = account.Balance.Amount - amount < -account.CreditLimit.Amount ? LedgerError.InsufficientFunds : (LedgerError?)null;
        return Apply(account.Debit(Money.Of(amount), "PIX_OUT", "REF", "client", recordedAt), entries, expected);
    }

    private static (LedgerError? Refusal, LedgerError? Expected) ChangeLimit(Account account, decimal limit)
    {
        var expected = account.Balance.Amount < -limit ? LedgerError.CreditLimitBelowBalance : (LedgerError?)null;
        return (account.ChangeCreditLimit(Money.Of(limit)), expected);
    }

    private static (LedgerError? Refusal, LedgerError? Expected) Apply(Outcome<Entry> outcome, List<Entry> entries, LedgerError? expected = null)
    {
        if (outcome.IsSuccess)
        {
            entries.Add(outcome.Value);
        }

        return (outcome.Error, expected);
    }

    private static (LedgerError? Refusal, LedgerError? Expected) ApplyReversal(
        Account account, List<Entry> entries, HashSet<Guid> reversed, int index, DateTimeOffset recordedAt)
    {
        var original = entries[index % entries.Count];
        var alreadyReversed = reversed.Contains(original.Id);
        LedgerError? expected = original.IsReversal ? LedgerError.EntryIsReversal
            : alreadyReversed ? LedgerError.EntryAlreadyReversed
            : original.Type == EntryType.Credit && account.Balance.Amount - original.Amount.Amount < -account.CreditLimit.Amount
                ? LedgerError.InsufficientFunds
                : null;

        var outcome = account.Reverse(original, alreadyReversed, "REV", "backoffice", recordedAt);
        if (outcome.IsSuccess)
        {
            reversed.Add(original.Id);
            outcome.Value.ReversedEntryId.ShouldBe(original.Id);
            outcome.Value.Type.ShouldBe(original.Type == EntryType.Credit ? EntryType.Debit : EntryType.Credit);
            outcome.Value.Amount.ShouldBe(original.Amount);
        }

        return Apply(outcome, entries, expected);
    }

    // Cadeia do ADR-0003: cada `balance_after` sai do anterior, a sequência é contígua e a conta reflete o último.
    private static void ShouldKeepChain(Account account, List<Entry> entries)
    {
        var balance = 0m;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            balance += entry.Type == EntryType.Credit ? entry.Amount.Amount : -entry.Amount.Amount;
            entry.BalanceAfter.Amount.ShouldBe(balance);
            entry.Sequence.ShouldBe(i + 1);
            entry.AccountId.ShouldBe(account.Id);
        }

        account.Balance.Amount.ShouldBe(balance);
        account.Version.ShouldBe(entries.Count);
    }

    public abstract record Operation
    {
        public sealed record Credit(decimal Amount) : Operation;

        public sealed record Debit(decimal Amount) : Operation;

        public sealed record Reverse(int Index) : Operation;

        public sealed record ChangeLimit(decimal Limit) : Operation;

        public sealed record DebitToLimit(int OffsetCents) : Operation;

        public sealed record ChangeLimitToBalance(int OffsetCents) : Operation;
    }

    // Valores de 0.01 a 500.00 e limites de 0 a 500.00, mais operações na borda exata do limite:
    // valores aleatórios quase nunca acertam o centavo da borda, onde mora o erro de `<` por `<=`.
    public static class Operations
    {
        public static Arbitrary<Operation[]> Sequences()
        {
            var amount = Gen.Choose(1, 50_000).Select(cents => cents / 100m);
            var limit = Gen.Choose(0, 50_000).Select(cents => cents / 100m);
            var operation = Gen.OneOf(
                amount.Select(value => (Operation)new Operation.Credit(value)),
                amount.Select(value => (Operation)new Operation.Debit(value)),
                Gen.Choose(0, 1_000).Select(index => (Operation)new Operation.Reverse(index)),
                limit.Select(value => (Operation)new Operation.ChangeLimit(value)),
                Gen.Choose(-1, 1).Select(offset => (Operation)new Operation.DebitToLimit(offset)),
                Gen.Choose(-1, 1).Select(offset => (Operation)new Operation.ChangeLimitToBalance(offset)));

            return Gen.Choose(1, 60).SelectMany(length => operation.ArrayOf(length)).ToArbitrary();
        }
    }
}
