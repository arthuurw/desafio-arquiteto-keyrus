using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IntegrationTests.Identity;

namespace IntegrationTests.Ledger;

// Conta criada pelo teste, com o titular que o token delegado precisa carregar.
internal sealed record TestAccount(Guid Id, Guid CustomerId);

// Chamadas HTTP às rotas do Ledger, com o corpo em JSON cru quando o teste precisa de um tipo errado.
internal static class LedgerClient
{
    internal static async Task<TestAccount> OpenAccountAsync(this HttpClient client, Guid? customerId = null)
    {
        var customer = customerId ?? Guid.NewGuid();
        using var response = await client.SendJsonAsync(HttpMethod.Post, "/v1/accounts", new { customerId = customer }, LedgerTokens.Backoffice);
        ((int)response.StatusCode).ShouldBe(201, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = await response.JsonAsync();
        return new TestAccount(body.GetProperty("id").GetGuid(), customer);
    }

    internal static Task<HttpResponseMessage> CreditAsync(
        this HttpClient client, Guid accountId, string amount, string category = "PIX_IN", string? externalReference = null, string? token = null) =>
        client.SendJsonAsync(
            HttpMethod.Post,
            $"/v1/accounts/{accountId}/credits",
            new { amount, category, externalReference = externalReference ?? NewReference() },
            token ?? LedgerTokens.PaymentsHub);

    internal static Task<HttpResponseMessage> DebitAsync(
        this HttpClient client, TestAccount account, string amount, string category = "PIX_OUT", string? externalReference = null) =>
        client.SendJsonAsync(
            HttpMethod.Post,
            $"/v1/accounts/{account.Id}/debits",
            new { amount, category, externalReference = externalReference ?? NewReference() },
            LedgerTokens.Channel(account.CustomerId));

    internal static Task<HttpResponseMessage> ReverseAsync(this HttpClient client, Guid accountId, Guid entryId, string? externalReference = null) =>
        client.SendJsonAsync(
            HttpMethod.Post,
            $"/v1/accounts/{accountId}/entries/{entryId}/reversal",
            new { externalReference = externalReference ?? NewReference() },
            LedgerTokens.Backoffice);

    internal static Task<HttpResponseMessage> SetCreditLimitAsync(this HttpClient client, Guid accountId, string creditLimit) =>
        client.SendJsonAsync(HttpMethod.Put, $"/v1/accounts/{accountId}/credit-limit", new { creditLimit }, LedgerTokens.Backoffice);

    // Crédito ou débito que precisa ser aceito para montar o cenário; devolve o lançamento.
    internal static async Task<JsonElement> CreditOkAsync(this HttpClient client, Guid accountId, string amount)
    {
        using var response = await client.CreditAsync(accountId, amount);
        return await response.ShouldBeEntryAsync();
    }

    internal static async Task<JsonElement> DebitOkAsync(this HttpClient client, TestAccount account, string amount)
    {
        using var response = await client.DebitAsync(account, amount);
        return await response.ShouldBeEntryAsync();
    }

    internal static async Task SetCreditLimitOkAsync(this HttpClient client, Guid accountId, string creditLimit)
    {
        using var response = await client.SetCreditLimitAsync(accountId, creditLimit);
        ((int)response.StatusCode).ShouldBe(204, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    internal static Task<HttpResponseMessage> SendJsonAsync(this HttpClient client, HttpMethod method, string route, object body, string? token) =>
        client.SendRawAsync(method, route, JsonSerializer.Serialize(body), token);

    internal static async Task<HttpResponseMessage> SendRawAsync(this HttpClient client, HttpMethod method, string route, string json, string? token)
    {
        using var request = new HttpRequestMessage(method, new Uri(route, UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    internal static string NewReference() => $"REF-{Guid.NewGuid():N}";
}
