using System.Globalization;
using System.Text.Json.Nodes;
using IntegrationTests.Identity;
using IntegrationTests.Ledger;

namespace IntegrationTests.Positions;

// Resposta de uma consulta: status e corpo cru, para comparar os dois caminhos byte a byte.
internal sealed record QueryResponse(int Status, string Text)
{
    internal JsonNode Body => JsonNode.Parse(Text).ShouldNotBeNull();

    internal JsonNode ShouldBeOk()
    {
        Status.ShouldBe(200, Text);
        return Body;
    }

    // Problem Details de validação com erro exatamente no campo.
    internal void ShouldBeValidationErrorOn(string field)
    {
        Status.ShouldBe(400, Text);
        Body["type"]!.GetValue<string>().ShouldBe("validation-error", Text);
        Body["errors"]!.AsObject().Select(error => error.Key).ShouldContain(field, Text);
    }
}

// Chamadas às rotas de Positions, com os tokens dos clients do realm local.
internal static class PositionsClient
{
    internal static string Compliance =>
        TestTokenIssuer.Sign(TestTokenIssuer.Service("compliance", "customers.pii.read customers.erasure.manage positions.read"));

    internal static Task<QueryResponse> BalanceAsync(this HttpClient client, Guid accountId, string? at = null, string? token = null) =>
        client.QueryAsync($"/v1/accounts/{accountId}/balance{Query(("at", at))}", token ?? LedgerTokens.Backoffice);

    internal static Task<QueryResponse> StatementAsync(
        this HttpClient client, Guid accountId, string? from = null, string? to = null, string? limit = null, string? cursor = null, string? token = null) =>
        client.QueryAsync(
            $"/v1/accounts/{accountId}/statement{Query(("from", from), ("to", to), ("limit", limit), ("cursor", cursor))}",
            token ?? LedgerTokens.Backoffice);

    internal static async Task<QueryResponse> QueryAsync(this HttpClient client, string route, string? token)
    {
        using var response = await client.GetAsync(route, token);
        return new QueryResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // Conta nova com `count` créditos de 1.00, 2.00, ... na ordem.
    internal static async Task<TestAccount> AccountWithCreditsAsync(this HttpClient client, int count)
    {
        var account = await client.OpenAccountAsync();
        for (var i = 1; i <= count; i++)
        {
            await client.CreditOkAsync(account.Id, $"{i}.00");
        }

        return account;
    }

    // Todas as páginas do extrato com `limit=2`, seguindo o `nextCursor`, como chegaram.
    internal static async Task<List<QueryResponse>> StatementPagesAsync(this HttpClient client, Guid accountId)
    {
        var pages = new List<QueryResponse>();
        string? cursor = null;
        do
        {
            var page = await client.StatementAsync(accountId, limit: "2", cursor: cursor);
            pages.Add(page);
            cursor = page.ShouldBeOk()["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null && pages.Count < 10);

        return pages;
    }

    internal static long[] Sequences(JsonNode page) =>
        page["items"]!.AsArray().Select(item => item!["sequence"]!.GetValue<long>()).ToArray();

    // Instante em ISO 8601 com microssegundos, em UTC `Z`.
    internal static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    internal static string Iso(DateTimeOffset instant) => Iso(instant.UtcDateTime);

    private static string Query(params (string Name, string? Value)[] parameters)
    {
        var present = parameters.Where(parameter => parameter.Value is not null)
            .Select(parameter => $"{parameter.Name}={Uri.EscapeDataString(parameter.Value!)}")
            .ToList();
        return present.Count == 0 ? string.Empty : "?" + string.Join('&', present);
    }
}
