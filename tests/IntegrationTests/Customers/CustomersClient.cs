using System.Security.Cryptography;
using System.Text.Json;
using IntegrationTests.Identity;
using IntegrationTests.Ledger;
using static IntegrationTests.Identity.TestTokenIssuer;

namespace IntegrationTests.Customers;

// Corpo do cadastro como o chamador envia.
internal sealed record CustomerInput(string Name, string Cpf, string BirthDate, string Email);

// Tokens dos clients que chamam Customers no realm local, assinados a cada uso.
internal static class CustomersTokens
{
    internal static string Backoffice => LedgerTokens.Backoffice;

    internal static string Compliance => Sign(Service("compliance", "customers.pii.read"));

    internal static string Only(string scope) => Sign(Service("backoffice", scope));

    internal static string Delegated(Guid customerId) =>
        Sign(Identity.TestTokenIssuer.Delegated(customerId.ToString(), "customers.read customers.pii.read"));
}

// Chamadas HTTP às rotas de Customers.
internal static class CustomersClient
{
    // A cliente dos exemplos do ADR-0042, de valores fixos: os testes afirmam a máscara e a leitura sobre eles.
    internal static CustomerInput Ana { get; } = new("Ana Maria Souza", "529.982.247-25", "1990-04-15", "ana.souza@ledger.local");

    // CPF válido sorteado: cada teste cadastra um cliente que nenhum outro usa.
    internal static string NewCpf()
    {
        while (true)
        {
            var digits = Enumerable.Range(0, 9).Select(_ => RandomNumberGenerator.GetInt32(10)).ToList();
            if (digits.Distinct().Count() == 1)
            {
                continue;
            }

            digits.Add(CheckDigit(digits));
            digits.Add(CheckDigit(digits));
            return string.Concat(digits);
        }
    }

    internal static CustomerInput NewCustomer() =>
        new("Carla Dias Moreira", NewCpf(), "1988-07-21", $"carla.{Guid.NewGuid():N}@ledger.local");

    internal static Task<HttpResponseMessage> RegisterAsync(
        this HttpClient client, CustomerInput customer, KeyHeader idempotencyKey = default, string? token = null) =>
        client.SendJsonAsync(
            HttpMethod.Post,
            "/v1/customers",
            new { name = customer.Name, cpf = customer.Cpf, birthDate = customer.BirthDate, email = customer.Email },
            token ?? CustomersTokens.Backoffice,
            idempotencyKey);

    // Cadastro que precisa ser aceito para montar o cenário; devolve o id.
    internal static async Task<Guid> RegisteredAsync(this HttpClient client, CustomerInput customer)
    {
        using var response = await client.RegisterAsync(customer);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        ((int)response.StatusCode).ShouldBe(201, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
    }

    internal static Task<HttpResponseMessage> MaskedAsync(this HttpClient client, object id, string? token) =>
        client.GetAsync($"/v1/customers/{id}", token);

    internal static Task<HttpResponseMessage> PersonalDataAsync(this HttpClient client, object id, string? token) =>
        client.GetAsync($"/v1/customers/{id}/personal-data", token);

    internal static async Task<JsonElement> ShouldBeCustomerAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        ((int)response.StatusCode).ShouldBe(200, text);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/json");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    internal static string? Header(this HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static int CheckDigit(List<int> digits)
    {
        var sum = digits.Select((digit, index) => digit * (digits.Count + 1 - index)).Sum();
        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }
}
