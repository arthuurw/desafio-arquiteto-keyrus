using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Positions.Features;

// Lê a query string campo a campo: valor fora do formato vira erro do campo, para o `400` dizer qual (ADR-0042).
internal sealed partial class PositionsQuery(IQueryCollection query)
{
    private readonly Dictionary<string, string[]> _errors = new(StringComparer.Ordinal);

    internal bool IsValid => _errors.Count == 0;

    internal IReadOnlyDictionary<string, string[]> Errors => _errors;

    // Instante ISO 8601 com offset explícito (`Z` ou `±hh:mm`), em UTC: sem offset, o instante dependeria do fuso de
    // quem envia. Volta com a precisão pedida; quem compara com `recordedAt` ajusta ao microssegundo (`Microsecond`).
    internal DateTimeOffset? Instant(string field)
    {
        const string Message = "Informe um instante ISO 8601 com offset, como 2026-10-01T12:00:00Z ou 2026-10-01T09:00:00-03:00.";
        if (!TryRead(field, Message, out var value) || value is null)
        {
            return null;
        }

        if (!InstantPattern().IsMatch(value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
        {
            Fail(field, Message);
            return null;
        }

        return instant.ToUniversalTime();
    }

    internal int Limit(string field, int defaultValue, int max)
    {
        var message = $"Informe um inteiro de 1 a {max}.";
        if (!TryRead(field, message, out var value) || value is null)
        {
            return defaultValue;
        }

        var limit = LimitPattern().IsMatch(value) ? int.Parse(value, CultureInfo.InvariantCulture) : 0;
        if (limit < 1 || limit > max)
        {
            Fail(field, message);
            return defaultValue;
        }

        return limit;
    }

    internal StatementCursor? Cursor(string field)
    {
        const string Message = "Informe o nextCursor devolvido pela página anterior.";
        if (!TryRead(field, Message, out var value) || value is null)
        {
            return null;
        }

        if (StatementCursor.TryParse(value, out var cursor))
        {
            return cursor;
        }

        Fail(field, Message);
        return null;
    }

    // O primeiro erro de um campo é o que vale: ele explica o formato esperado.
    internal void Fail(string field, string message) => _errors.TryAdd(field, [message]);

    // Campo repetido também é erro: não há como saber qual valor vale.
    private bool TryRead(string field, string message, out string? value)
    {
        value = null;
        if (!query.TryGetValue(field, out var values))
        {
            return true;
        }

        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            Fail(field, message);
            return false;
        }

        value = values[0];
        return true;
    }

    // `[0-9]` e `\z`: `\d` aceitaria dígitos de outros alfabetos e `$` aceitaria uma quebra de linha final.
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstantPattern();

    [GeneratedRegex(@"^[0-9]{1,4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LimitPattern();
}
