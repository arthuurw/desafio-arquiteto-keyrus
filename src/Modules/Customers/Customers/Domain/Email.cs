using System.Text.RegularExpressions;

namespace Customers.Domain;

// E-mail de contato, aparado: uma `@`, parte local e domínio com ao menos um ponto, até 254 caracteres (RFC 5321).
internal sealed partial record Email
{
    internal const int MaxLength = 254;

    private Email(string value) => Value = value;

    internal string Value { get; }

    internal static Email? Parse(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxLength && Pattern().IsMatch(trimmed) ? new Email(trimmed) : null;
    }

    // `\z` e não `$`: `$` aceitaria uma quebra de linha final.
    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
