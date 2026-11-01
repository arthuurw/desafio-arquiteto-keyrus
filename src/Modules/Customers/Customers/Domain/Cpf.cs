namespace Customers.Domain;

// CPF com os dígitos verificadores conferidos, guardado só com os 11 dígitos: com ou sem pontuação é o mesmo CPF.
internal sealed record Cpf
{
    private Cpf(string digits) => Digits = digits;

    internal string Digits { get; }

    // `000.000.000-00`, a forma devolvida na leitura completa.
    internal string Formatted => $"{Digits[..3]}.{Digits[3..6]}.{Digits[6..9]}-{Digits[9..]}";

    // Aceita só os 11 dígitos ou a forma pontuada completa; qualquer outra mistura é recusada.
    internal static Cpf? Parse(string value)
    {
        var digits = value.Length switch
        {
            11 => value,
            14 when value[3] == '.' && value[7] == '.' && value[11] == '-' =>
                string.Concat(value.AsSpan(0, 3), value.AsSpan(4, 3), value.AsSpan(8, 3), value.AsSpan(12, 2)),
            _ => null,
        };

        if (digits is null || digits.Length != 11 || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        // Dígitos todos iguais passam no cálculo do verificador, mas não são CPF.
        if (digits.All(digit => digit == digits[0]))
        {
            return null;
        }

        return CheckDigit(digits, 9) == digits[9] - '0' && CheckDigit(digits, 10) == digits[10] - '0' ? new Cpf(digits) : null;
    }

    // Módulo 11 sobre os `length` primeiros dígitos, com pesos de `length + 1` a 2.
    private static int CheckDigit(string digits, int length)
    {
        var sum = 0;
        for (var index = 0; index < length; index++)
        {
            sum += (digits[index] - '0') * (length + 1 - index);
        }

        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }
}
