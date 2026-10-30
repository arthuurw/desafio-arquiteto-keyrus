using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ledger.Domain;
using Microsoft.AspNetCore.Http;

namespace Ledger.Features;

// Lê o corpo campo a campo: um número JSON onde se espera string vira erro do campo, não falha de desserialização.
internal sealed partial class LedgerInput
{
    private readonly JsonElement _body;
    private readonly Dictionary<string, string[]> _errors = new(StringComparer.Ordinal);

    private LedgerInput(JsonElement body) => _body = body;

    internal bool IsValid => _errors.Count == 0;

    internal IReadOnlyDictionary<string, string[]> Errors => _errors;

    internal static async Task<LedgerInput> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            var input = new LedgerInput(document.RootElement.Clone());
            if (input._body.ValueKind != JsonValueKind.Object)
            {
                input._errors["body"] = ["O corpo da requisição deve ser um objeto JSON."];
            }

            return input;
        }
        catch (JsonException)
        {
            var input = new LedgerInput(default);
            input._errors["body"] = ["O corpo da requisição deve ser um objeto JSON."];
            return input;
        }
    }

    // Dinheiro chega como string: número JSON vira ponto flutuante em muitos clientes (ADR-0018).
    internal Money? Amount(string field) =>
        ReadMoney(field, allowZero: false, "Informe uma string decimal maior que zero, com até 2 casas e até 999999999999.99.");

    internal Money? CreditLimit(string field) =>
        ReadMoney(field, allowZero: true, "Informe uma string decimal maior ou igual a zero, com até 2 casas e até 999999999999.99.");

    // Catálogo fechado por tipo; `REVERSAL` nunca vem do chamador (ADR-0017).
    internal string? Category(string field, EntryType type)
    {
        var allowed = type == EntryType.Credit ? Categories.Credit : Categories.Debit;
        var message = $"Informe uma categoria de {(type == EntryType.Credit ? "crédito" : "débito")}: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.";
        var value = ReadString(field, message);
        return value is null || Categories.Accepts(type, value) ? value : Fail<string>(field, message);
    }

    // Sem espaço nem acento: reduz o risco de um nome de pessoa entrar como referência (ADR-0017).
    internal string? ExternalReference(string field)
    {
        const string Message = "Informe de 1 a 64 caracteres entre letras sem acento, dígitos e . _ : -";
        var value = ReadString(field, Message);
        return value is null || ExternalReferencePattern().IsMatch(value) ? value : Fail<string>(field, Message);
    }

    internal Guid? CustomerId(string field)
    {
        const string Message = "Informe o identificador UUID do cliente.";
        var value = ReadString(field, Message);
        if (value is null)
        {
            return null;
        }

        return Guid.TryParseExact(value, "D", out var customerId) && customerId != Guid.Empty ? customerId : Fail(field, Message);
    }

    private Money? ReadMoney(string field, bool allowZero, string message)
    {
        var value = ReadString(field, message);
        if (value is null)
        {
            return null;
        }

        if (!MoneyPattern().IsMatch(value))
        {
            return Fail<Money>(field, message);
        }

        var amount = decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return amount > 0m || (allowZero && amount == 0m) ? Money.Of(amount) : Fail<Money>(field, message);
    }

    // Campo ausente ou que não é string JSON vira erro do campo; corpo inválido já foi apontado.
    private string? ReadString(string field, string message)
    {
        if (_body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return _body.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : Fail<string>(field, message);
    }

    private T? Fail<T>(string field, string message)
        where T : class
    {
        _errors[field] = [message];
        return null;
    }

    private Guid? Fail(string field, string message)
    {
        _errors[field] = [message];
        return null;
    }

    // Até 12 dígitos inteiros e 2 casas: teto 999999999999.99, com folga no `numeric(19,4)`.
    // `[0-9]` e `\z`: `\d` aceitaria dígitos de outros alfabetos e `$` aceitaria uma quebra de linha final.
    [GeneratedRegex(@"^[0-9]{1,12}(\.[0-9]{1,2})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex MoneyPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExternalReferencePattern();
}
