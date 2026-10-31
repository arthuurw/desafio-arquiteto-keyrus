using Ledger.Domain;

namespace Ledger.UnitTests;

public sealed class AccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Customer = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public void OpensWithZeroBalanceLimitAndVersion()
    {
        var account = Account.Open(Customer);

        account.Id.Version.ShouldBe(7);
        account.CustomerId.ShouldBe(Customer);
        account.Balance.ShouldBe(Money.Zero);
        account.CreditLimit.ShouldBe(Money.Zero);
        account.Version.ShouldBe(0);
        account.LastRecordedAt.ShouldBeNull();
    }

    [Fact]
    public void CreditProducesCompleteEntry()
    {
        var account = Account.Open(Customer);

        var entry = account.Credit(Money.Of(150.75m), "PIX_IN", "E2E-1", "payments-hub", Now).Value!;

        entry.Id.Version.ShouldBe(7);
        entry.AccountId.ShouldBe(account.Id);
        entry.Sequence.ShouldBe(1);
        entry.Type.ShouldBe(EntryType.Credit);
        entry.Amount.ShouldBe(Money.Of(150.75m));
        entry.BalanceAfter.ShouldBe(Money.Of(150.75m));
        entry.Category.ShouldBe("PIX_IN");
        entry.ExternalReference.ShouldBe("E2E-1");
        entry.ClientId.ShouldBe("payments-hub");
        entry.ReversedEntryId.ShouldBeNull();
        entry.IsReversal.ShouldBeFalse();
        entry.RecordedAt.ShouldBe(Now);
        account.Balance.ShouldBe(Money.Of(150.75m));
        account.Version.ShouldBe(1);
        account.LastRecordedAt.ShouldBe(Now);
    }

    [Fact]
    public void CreditIsAcceptedBelowZeroWhateverTheLimit()
    {
        var account = AccountWithBalance(-500.00m, limit: 500.00m);

        var entry = account.Credit(Money.Of(0.01m), "DEPOSIT", "R", "c", Now).Value!;

        entry.BalanceAfter.ShouldBe(Money.Of(-499.99m));
    }

    [Fact]
    public void DebitDownToMinusLimitIsAcceptedAndOneCentMoreIsRefused()
    {
        var account = Account.Open(Customer);
        account.Credit(Money.Of(100.00m), "PIX_IN", "R", "c", Now);
        account.ChangeCreditLimit(Money.Of(50.00m)).ShouldBeNull();

        var accepted = account.Debit(Money.Of(150.00m), "PIX_OUT", "R", "c", Now);
        var refused = account.Debit(Money.Of(0.01m), "PIX_OUT", "R", "c", Now);

        accepted.Value!.BalanceAfter.ShouldBe(Money.Of(-50.00m));
        accepted.Value.Type.ShouldBe(EntryType.Debit);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error.ShouldBe(LedgerError.InsufficientFunds);
        account.Balance.ShouldBe(Money.Of(-50.00m));
        account.Version.ShouldBe(2);
    }

    [Fact]
    public void DebitToExactlyZeroWithZeroLimitIsAccepted()
    {
        var account = AccountWithBalance(100.00m, limit: 0m);

        var entry = account.Debit(Money.Of(100.00m), "CARD", "R", "c", Now).Value!;

        entry.BalanceAfter.ShouldBe(Money.Zero);
    }

    [Fact]
    public void RefusedDebitKeepsRecordingInstant()
    {
        var account = AccountWithBalance(10.00m, limit: 0m);
        var later = Now.AddHours(1);

        var refused = account.Debit(Money.Of(10.01m), "CARD", "R", "c", later);

        refused.Error.ShouldBe(LedgerError.InsufficientFunds);
        account.LastRecordedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1.00")]
    public void EntryAmountMustBePositive(string value)
    {
        var account = Account.Open(Customer);
        var amount = Money.Of(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

        var refusal = Should.Throw<ArgumentOutOfRangeException>(() => account.Credit(amount, "PIX_IN", "R", "c", Now));
        refusal.ParamName.ShouldBe("amount");
        refusal.Message.ShouldStartWith("An entry amount must be greater than zero.");
        Should.Throw<ArgumentOutOfRangeException>(() => account.Debit(amount, "PIX_OUT", "R", "c", Now)).ParamName.ShouldBe("amount");
        account.Version.ShouldBe(0);
    }

    [Theory]
    [InlineData("PIX_OUT")]
    [InlineData("REVERSAL")]
    [InlineData("pix_in")]
    public void CreditRefusesCategoryOutsideItsCatalog(string category)
    {
        var account = Account.Open(Customer);

        var refusal = Should.Throw<ArgumentException>(() => account.Credit(Money.Of(1m), category, "R", "c", Now));
        refusal.ParamName.ShouldBe("category");
        refusal.Message.ShouldStartWith($"Category '{category}' is not valid for a Credit entry.");
        account.Version.ShouldBe(0);
    }

    [Theory]
    [InlineData("PIX_IN")]
    [InlineData("REVERSAL")]
    public void DebitRefusesCategoryOutsideItsCatalog(string category)
    {
        var account = AccountWithBalance(100m, limit: 0m);

        Should.Throw<ArgumentException>(() => account.Debit(Money.Of(1m), category, "R", "c", Now)).ParamName.ShouldBe("category");
        account.Version.ShouldBe(1);
    }

    [Fact]
    public void EntryCannotBeRecordedBeforeThePreviousOne()
    {
        var account = AccountWithBalance(100m, limit: 0m);

        var refusal = Should.Throw<ArgumentOutOfRangeException>(() => account.Credit(Money.Of(1m), "PIX_IN", "R", "c", Now.AddTicks(-1)));
        refusal.ParamName.ShouldBe("recordedAt");
        refusal.Message.ShouldStartWith("An entry cannot be recorded before the previous one.");
        account.Credit(Money.Of(1m), "PIX_IN", "R", "c", Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ReversalOfCreditIsDebitOfSameAmount()
    {
        var account = Account.Open(Customer);
        var credit = account.Credit(Money.Of(100.00m), "PIX_IN", "E2E-1", "payments-hub", Now).Value!;

        var reversal = account.Reverse(credit, alreadyReversed: false, "REV-1", "backoffice", Now).Value!;

        reversal.Type.ShouldBe(EntryType.Debit);
        reversal.Amount.ShouldBe(Money.Of(100.00m));
        reversal.Category.ShouldBe(Categories.Reversal);
        reversal.ReversedEntryId.ShouldBe(credit.Id);
        reversal.IsReversal.ShouldBeTrue();
        reversal.ExternalReference.ShouldBe("REV-1");
        reversal.ClientId.ShouldBe("backoffice");
        reversal.BalanceAfter.ShouldBe(Money.Zero);
        reversal.Sequence.ShouldBe(2);
    }

    [Fact]
    public void ReversalOfDebitIsCreditNeverRefusedByBalance()
    {
        var account = AccountWithBalance(0m, limit: 30m);
        var debit = account.Debit(Money.Of(30.00m), "CARD", "R", "c", Now).Value!;

        var reversal = account.Reverse(debit, alreadyReversed: false, "REV", "backoffice", Now).Value!;

        reversal.Type.ShouldBe(EntryType.Credit);
        reversal.Amount.ShouldBe(Money.Of(30.00m));
        reversal.BalanceAfter.ShouldBe(Money.Zero);
    }

    [Fact]
    public void ReversalOfCreditRespectsLimit()
    {
        var account = Account.Open(Customer);
        var credit = account.Credit(Money.Of(100.00m), "PIX_IN", "R", "c", Now).Value!;
        account.Debit(Money.Of(80.00m), "PIX_OUT", "R", "c", Now);

        var refused = account.Reverse(credit, alreadyReversed: false, "REV", "backoffice", Now);

        refused.Error.ShouldBe(LedgerError.InsufficientFunds);
        account.Balance.ShouldBe(Money.Of(20.00m));
        account.Version.ShouldBe(2);
    }

    [Fact]
    public void EntryIsReversedOnlyOnceAndReversalIsNeverReversed()
    {
        var account = Account.Open(Customer);
        var credit = account.Credit(Money.Of(100.00m), "PIX_IN", "R", "c", Now).Value!;
        var reversal = account.Reverse(credit, alreadyReversed: false, "REV", "backoffice", Now).Value!;

        var again = account.Reverse(credit, alreadyReversed: true, "REV-2", "backoffice", Now);
        var ofReversal = account.Reverse(reversal, alreadyReversed: false, "REV-3", "backoffice", Now);
        // Estornar um estorno é recusado mesmo que ele próprio já tenha estorno registrado.
        var ofReversedReversal = account.Reverse(reversal, alreadyReversed: true, "REV-4", "backoffice", Now);

        again.Error.ShouldBe(LedgerError.EntryAlreadyReversed);
        ofReversal.Error.ShouldBe(LedgerError.EntryIsReversal);
        ofReversedReversal.Error.ShouldBe(LedgerError.EntryIsReversal);
        account.Version.ShouldBe(2);
        account.Balance.ShouldBe(Money.Zero);
    }

    [Fact]
    public void ReversalRefusesEntryOfAnotherAccount()
    {
        var account = Account.Open(Customer);
        var other = Account.Open(Customer);
        var foreign = other.Credit(Money.Of(10m), "PIX_IN", "R", "c", Now).Value!;

        var refusal = Should.Throw<ArgumentException>(() => account.Reverse(foreign, alreadyReversed: false, "REV", "backoffice", Now));
        refusal.ParamName.ShouldBe("original");
        refusal.Message.ShouldStartWith("The entry belongs to another account.");
        account.Version.ShouldBe(0);
    }

    [Fact]
    public void CreditLimitMustCoverBalanceAlreadyBelowZero()
    {
        var account = AccountWithBalance(-300.00m, limit: 300.00m);

        var refused = account.ChangeCreditLimit(Money.Of(299.99m));
        var limitAfterRefusal = account.CreditLimit;
        var accepted = account.ChangeCreditLimit(Money.Of(300.00m));

        refused.ShouldBe(LedgerError.CreditLimitBelowBalance);
        limitAfterRefusal.ShouldBe(Money.Of(300.00m));
        accepted.ShouldBeNull();
        account.CreditLimit.ShouldBe(Money.Of(300.00m));
        account.Version.ShouldBe(1);
    }

    [Fact]
    public void CreditLimitChangeDoesNotChangeVersion()
    {
        var account = Account.Open(Customer);

        account.ChangeCreditLimit(Money.Of(500.00m)).ShouldBeNull();
        account.ChangeCreditLimit(Money.Zero).ShouldBeNull();

        account.CreditLimit.ShouldBe(Money.Zero);
        account.Version.ShouldBe(0);
        account.Balance.ShouldBe(Money.Zero);
    }

    [Fact]
    public void CreditLimitCannotBeNegative()
    {
        var account = Account.Open(Customer);

        var refusal = Should.Throw<ArgumentOutOfRangeException>(() => account.ChangeCreditLimit(Money.Of(-0.01m)));
        refusal.ParamName.ShouldBe("creditLimit");
        refusal.Message.ShouldStartWith("The credit limit cannot be negative.");
        account.CreditLimit.ShouldBe(Money.Zero);
    }

    [Fact]
    public void CategoryCatalogIsClosedPerType()
    {
        Categories.Credit.Order(StringComparer.Ordinal).ShouldBe(["ADJUSTMENT_IN", "BOLETO_IN", "DEPOSIT", "PIX_IN", "TED_IN"]);
        Categories.Debit.Order(StringComparer.Ordinal).ShouldBe(["ADJUSTMENT_OUT", "BOLETO_OUT", "CARD", "FEE", "PIX_OUT", "TED_OUT"]);
        Categories.Accepts(EntryType.Credit, "PIX_IN").ShouldBeTrue();
        Categories.Accepts(EntryType.Debit, "PIX_IN").ShouldBeFalse();
        Categories.Accepts(EntryType.Debit, "CARD").ShouldBeTrue();
        Categories.Accepts(EntryType.Credit, "CARD").ShouldBeFalse();
        Categories.Accepts(EntryType.Credit, Categories.Reversal).ShouldBeFalse();
        Categories.Accepts(EntryType.Debit, Categories.Reversal).ShouldBeFalse();
    }

    // Teto do crédito e do estorno de débito, que também credita: um centavo abaixo passa, um real acima não.
    [Fact]
    public void CreditAboveMaxBalanceIsRefused()
    {
        var account = AccountWithBalance(999999999999999.00m, limit: 0m);
        var nearMax = AccountWithBalance(999999999999999.99m, limit: 0m);
        var debit = nearMax.Debit(Money.Of(1.00m), "PIX_OUT", "D", "c", Now).Value!;
        nearMax.Credit(Money.Of(1.00m), "PIX_IN", "C", "c", Now).IsSuccess.ShouldBeTrue();

        var refused = account.Credit(Money.Of(1.00m), "PIX_IN", "R1", "c", Now);
        var balanceAfterRefusal = account.Balance;
        var versionAfterRefusal = account.Version;
        var accepted = account.Credit(Money.Of(0.99m), "PIX_IN", "R2", "c", Now);
        var refusedReversal = nearMax.Reverse(debit, alreadyReversed: false, "REV", "backoffice", Now);

        Account.MaxBalance.ShouldBe(Money.Of(999999999999999.99m));
        refused.Error.ShouldBe(LedgerError.BalanceLimitExceeded);
        balanceAfterRefusal.ShouldBe(Money.Of(999999999999999.00m));
        versionAfterRefusal.ShouldBe(1);
        accepted.IsSuccess.ShouldBeTrue();
        account.Balance.ShouldBe(Money.Of(999999999999999.99m));
        account.Version.ShouldBe(2);
        refusedReversal.Error.ShouldBe(LedgerError.BalanceLimitExceeded);
        nearMax.Balance.ShouldBe(Money.Of(999999999999999.99m));
        nearMax.Version.ShouldBe(3);
    }

    [Fact]
    public void OppositeSwapsCreditAndDebit()
    {
        EntryType.Credit.Opposite().ShouldBe(EntryType.Debit);
        EntryType.Debit.Opposite().ShouldBe(EntryType.Credit);
    }

    // Conta com o saldo pedido, alcançado por um crédito ou por um débito dentro do limite.
    private static Account AccountWithBalance(decimal balance, decimal limit)
    {
        var account = Account.Open(Customer);
        account.ChangeCreditLimit(Money.Of(limit)).ShouldBeNull();
        if (balance > 0)
        {
            account.Credit(Money.Of(balance), "PIX_IN", "SETUP", "c", Now).IsSuccess.ShouldBeTrue();
        }
        else if (balance < 0)
        {
            account.Debit(Money.Of(-balance), "PIX_OUT", "SETUP", "c", Now).IsSuccess.ShouldBeTrue();
        }

        return account;
    }
}
