using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Ledger.Domain;

namespace Ledger.UnitTests;

public sealed class MoneyProperties
{
    [Property(MaxTest = 1000, Arbitrary = [typeof(TwoDecimalPlaces)])]
    public bool AddThenSubtractIsIdentity(decimal a, decimal b)
    {
        var left = Money.Of(a);
        var right = Money.Of(b);

        var result = left + right - right;

        return result == left && result.Amount == a;
    }

    // Valores com 2 casas, positivos e negativos, até a casa dos bilhões.
    public static class TwoDecimalPlaces
    {
        public static Arbitrary<decimal> Amounts() =>
            Gen.Choose(-999_999, 999_999)
                .Two()
                .Select(pair => (pair.Item1 * 1_000_000L + pair.Item2) / 100m)
                .ToArbitrary();
    }
}
