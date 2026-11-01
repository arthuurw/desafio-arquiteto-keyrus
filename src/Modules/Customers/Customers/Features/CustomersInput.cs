using System.Text.Json;
using Customers.Domain;
using Microsoft.AspNetCore.Http;

namespace Customers.Features;

// Lê o corpo campo a campo, como o LedgerInput (ADR-0030): um número JSON onde se espera string vira erro do campo, e
// todos os erros saem juntos. As mensagens nunca repetem o valor recebido, que pode ser dado pessoal.
internal sealed class CustomersInput
{
    private readonly JsonElement _body;
    private readonly Dictionary<string, string[]> _errors = new(StringComparer.Ordinal);

    private CustomersInput(JsonElement body) => _body = body;

    internal bool IsValid => _errors.Count == 0;

    internal IReadOnlyDictionary<string, string[]> Errors => _errors;

    internal static async Task<CustomersInput> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            var input = new CustomersInput(document.RootElement.Clone());
            if (input._body.ValueKind != JsonValueKind.Object)
            {
                input._errors["body"] = ["O corpo da requisição deve ser um objeto JSON."];
            }

            return input;
        }
        catch (JsonException)
        {
            var input = new CustomersInput(default);
            input._errors["body"] = ["O corpo da requisição deve ser um objeto JSON."];
            return input;
        }
    }

    internal CustomerName? Name(string field) =>
        Read(field, $"Informe o nome, com 1 a {CustomerName.MaxLength} caracteres.", CustomerName.Parse);

    internal Cpf? Cpf(string field) =>
        Read(field, "Informe um CPF válido, com 11 dígitos ou no formato 000.000.000-00.", Domain.Cpf.Parse);

    internal BirthDate? BirthDate(string field, DateTimeOffset now) =>
        Read(
            field,
            $"Informe a data de nascimento no formato yyyy-MM-dd, a partir de 1900-01-01, de um cliente com {Domain.BirthDate.AdultAge} anos ou mais.",
            value => Domain.BirthDate.Parse(value, now));

    internal Email? Email(string field) =>
        Read(field, $"Informe um e-mail válido, com até {Domain.Email.MaxLength} caracteres.", Domain.Email.Parse);

    // Campo ausente ou que não é string JSON vira erro do campo, também quando o corpo nem é objeto.
    private T? Read<T>(string field, string message, Func<string, T?> parse)
        where T : class
    {
        var value = _body.ValueKind == JsonValueKind.Object
            && _body.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String
            ? parse(property.GetString()!)
            : null;
        if (value is null)
        {
            _errors[field] = [message];
        }

        return value;
    }
}
