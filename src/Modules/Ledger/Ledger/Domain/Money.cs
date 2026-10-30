using System.Globalization;

namespace Ledger.Domain;

// Valor monetário exato em BRL (ADR-0018). Aceita negativo porque também representa saldo.
internal sealed record Money : IComparable<Money>
{
    internal const string Brl = "BRL";

    private Money(decimal amount) => Amount = amount;

    internal decimal Amount { get; }

    internal string Currency { get; } = Brl;

    internal static Money Zero { get; } = new(0m);

    internal bool IsPositive => Amount > 0m;

    internal bool IsNegative => Amount < 0m;

    // Nunca arredonda: valor com mais de 2 casas significativas é recusado, não ajustado.
    internal static Money Of(decimal amount, string currency = Brl)
    {
        if (!string.Equals(currency, Brl, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Only {Brl} is supported.", nameof(currency));
        }

        if (decimal.Round(amount, 2) != amount)
        {
            throw new ArgumentException("Money accepts at most 2 decimal places.", nameof(amount));
        }

        return new Money(amount);
    }

    public static Money operator +(Money left, Money right) => new(left.Amount + right.Amount);

    public static Money operator -(Money left, Money right) => new(left.Amount - right.Amount);

    public static Money operator -(Money value) => new(-value.Amount);

    public static bool operator <(Money left, Money right) => left.Amount < right.Amount;

    public static bool operator >(Money left, Money right) => left.Amount > right.Amount;

    public static bool operator <=(Money left, Money right) => left.Amount <= right.Amount;

    public static bool operator >=(Money left, Money right) => left.Amount >= right.Amount;

    public int CompareTo(Money? other) => other is null ? 1 : Amount.CompareTo(other.Amount);

    // Sempre 2 casas: o banco guarda 4, mas nenhum valor aceito passa de 2.
    public override string ToString() => Amount.ToString("0.00", CultureInfo.InvariantCulture);
}
