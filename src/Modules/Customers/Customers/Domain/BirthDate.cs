using System.Globalization;

namespace Customers.Domain;

// Data de nascimento de um cliente maior de idade: a conta de menor exige responsável legal, fora do cadastro.
internal sealed record BirthDate
{
    internal const int AdultAge = 18;

    internal static readonly DateOnly Earliest = new(1900, 1, 1);

    private BirthDate(DateOnly value) => Value = value;

    internal DateOnly Value { get; }

    internal string Iso => Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Só `yyyy-MM-dd`; a idade é contada no dia de hoje em UTC.
    internal static BirthDate? Parse(string value, DateTimeOffset now)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return null;
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return date >= Earliest && date <= today.AddYears(-AdultAge) ? new BirthDate(date) : null;
    }
}
