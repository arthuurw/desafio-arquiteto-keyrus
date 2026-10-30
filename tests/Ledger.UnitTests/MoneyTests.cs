using Ledger.Domain;

namespace Ledger.UnitTests;

public sealed class MoneyTests
{
    [Fact]
    public void AddsCentsWithoutBinaryRoundingError()
    {
        var sum = Money.Of(0.10m) + Money.Of(0.20m);

        sum.ShouldBe(Money.Of(0.30m));
        sum.Amount.ShouldBe(0.30m);
        sum.ToString().ShouldBe("0.30");
    }

    [Fact]
    public void SubtractsIntoNegativeBalance()
    {
        var balance = Money.Of(100.00m) - Money.Of(150.75m);

        balance.Amount.ShouldBe(-50.75m);
        balance.IsNegative.ShouldBeTrue();
        balance.IsPositive.ShouldBeFalse();
        balance.ToString().ShouldBe("-50.75");
        (-balance).Amount.ShouldBe(50.75m);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("brl")]
    [InlineData("")]
    public void RefusesCurrencyOtherThanBrl(string currency)
    {
        var refusal = Should.Throw<ArgumentException>(() => Money.Of(10m, currency));

        refusal.ParamName.ShouldBe("currency");
        refusal.Message.ShouldStartWith("Only BRL is supported.");
    }

    [Theory]
    [InlineData("10.001")]
    [InlineData("0.005")]
    [InlineData("-1.999")]
    public void RefusesMoreThanTwoDecimalPlaces(string value)
    {
        var amount = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        var refusal = Should.Throw<ArgumentException>(() => Money.Of(amount));

        refusal.ParamName.ShouldBe("amount");
        refusal.Message.ShouldStartWith("Money accepts at most 2 decimal places.");
    }

    [Theory]
    [InlineData("10.10", "10.10")]
    [InlineData("10.1", "10.10")]
    [InlineData("150.7500", "150.75")]
    [InlineData("0", "0.00")]
    public void AcceptsTwoSignificantDecimalPlacesAndFormatsWithTwo(string value, string formatted)
    {
        var money = Money.Of(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

        money.Currency.ShouldBe("BRL");
        money.ToString().ShouldBe(formatted);
    }

    [Fact]
    public void ZeroIsNeitherPositiveNorNegative()
    {
        Money.Zero.Amount.ShouldBe(0m);
        Money.Zero.IsPositive.ShouldBeFalse();
        Money.Zero.IsNegative.ShouldBeFalse();
        Money.Of(0.01m).IsPositive.ShouldBeTrue();
        Money.Of(-0.01m).IsNegative.ShouldBeTrue();
    }

    [Fact]
    public void ComparesByAmount()
    {
        var small = Money.Of(1.00m);
        var large = Money.Of(1.01m);

        (small < large).ShouldBeTrue();
        (large > small).ShouldBeTrue();
        (small <= Money.Of(1m)).ShouldBeTrue();
        (small >= Money.Of(1m)).ShouldBeTrue();
        (large < small).ShouldBeFalse();
        (small > large).ShouldBeFalse();
        (large <= small).ShouldBeFalse();
        (small >= large).ShouldBeFalse();
        (small < Money.Of(1m)).ShouldBeFalse();
        (small > Money.Of(1m)).ShouldBeFalse();
        small.CompareTo(large).ShouldBeLessThan(0);
        large.CompareTo(small).ShouldBeGreaterThan(0);
        small.CompareTo(Money.Of(1.00m)).ShouldBe(0);
        small.CompareTo(null).ShouldBeGreaterThan(0);
    }
}
