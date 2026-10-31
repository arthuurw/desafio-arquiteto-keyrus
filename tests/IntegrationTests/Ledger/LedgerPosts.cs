using System.Text.Json.Nodes;

namespace IntegrationTests.Ledger;

// As quatro rotas `POST` do Ledger.
public enum LedgerRoute
{
    Opening,
    Credits,
    Debits,
    Reversal,
}

// Um `POST` do Ledger com corpo válido e o token do client que pode chamá-lo.
internal sealed record LedgerPost(LedgerRoute Route, string Path, object Body, string Token);

// As quatro rotas `POST` do Ledger, para as regras de idempotência que valem para todas.
internal static class LedgerPosts
{
    internal static readonly LedgerRoute[] Routes = Enum.GetValues<LedgerRoute>();

    // Referência nova a cada chamada: só a repetição do mesmo `LedgerPost` repete o conteúdo.
    internal static LedgerPost For(LedgerRoute route, TestAccount account, Guid entryId) => route switch
    {
        LedgerRoute.Opening => new(route, "/v1/accounts", new { customerId = Guid.NewGuid() }, LedgerTokens.Backoffice),
        LedgerRoute.Credits => new(
            route,
            $"/v1/accounts/{account.Id}/credits",
            new { amount = "10.00", category = "PIX_IN", externalReference = LedgerClient.NewReference() },
            LedgerTokens.PaymentsHub),
        LedgerRoute.Debits => new(
            route,
            $"/v1/accounts/{account.Id}/debits",
            new { amount = "10.00", category = "PIX_OUT", externalReference = LedgerClient.NewReference() },
            LedgerTokens.Channel(account.CustomerId)),
        LedgerRoute.Reversal => new(
            route,
            $"/v1/accounts/{account.Id}/entries/{entryId}/reversal",
            new { externalReference = LedgerClient.NewReference() },
            LedgerTokens.Backoffice),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown ledger route."),
    };

    internal static Task<HttpResponseMessage> SendAsync(this HttpClient client, LedgerPost post, KeyHeader idempotencyKey) =>
        client.SendJsonAsync(HttpMethod.Post, post.Path, post.Body, post.Token, idempotencyKey);

    // Conta com saldo e um crédito estornável: todas as rotas aceitam o corpo de `For`.
    internal static async Task<(TestAccount Account, Guid EntryId)> FundedAccountAsync(this HttpClient client)
    {
        var account = await client.OpenAccountAsync();
        var credit = await client.CreditOkAsync(account.Id, "100.00");
        return (account, credit.GetProperty("id").GetGuid());
    }

    // `201` de qualquer rota `POST`: o lançamento, ou `{ id }` na abertura.
    internal static async Task<JsonNode> ShouldBeCreatedAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(201, text);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/json");
        var body = JsonNode.Parse(text).ShouldNotBeNull();
        body["id"].ShouldNotBeNull();
        return body;
    }

    internal static async Task<JsonNode> BodyAsync(this HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

    // Corpo sem o `traceId`, que é da requisição que responde e muda de uma para outra.
    internal static async Task<JsonNode> BodyWithoutTraceAsync(this HttpResponseMessage response)
    {
        var body = await response.BodyAsync();
        if (body is JsonObject problem)
        {
            problem.Remove("traceId");
        }

        return body;
    }
}
