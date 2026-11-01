using System.Globalization;

namespace Customers.Domain;

// Máscara da leitura com `customers.read` (ADR-0039): o suficiente para conferir com o cliente, não para identificá-lo.
internal static class CustomerMask
{
    // Primeiro nome inteiro e a inicial de cada outro: `Ana Maria Souza` -> `Ana M. S.`.
    internal static string Name(string name)
    {
        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(1).Concat(words.Skip(1).Select(word => $"{First(word)}.")));
    }

    // Só os seis dígitos do meio: `52998224725` -> `***.982.247-**`.
    internal static string Cpf(string digits) => $"***.{digits[3..6]}.{digits[6..9]}-**";

    // Só o dia: `1990-04-15` -> `****-**-15`.
    internal static string BirthDate(string iso) => $"****-**-{iso[^2..]}";

    // Primeiro caractere e o domínio: `ana.souza@ledger.local` -> `a***@ledger.local`.
    internal static string Email(string email) => $"{First(email)}***{email[email.IndexOf('@', StringComparison.Ordinal)..]}";

    // O primeiro caractere como o leitor o vê: `word[0]` partiria um caractere fora do BMP ao meio.
    private static string First(string text) => text[..StringInfo.GetNextTextElementLength(text)];
}
