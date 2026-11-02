using IntegrationTests.Infrastructure;
using static IntegrationTests.Infrastructure.AuditRows;
using static IntegrationTests.Infrastructure.LargeBodies;

namespace IntegrationTests.Ledger;

// Corpo acima de 16 384 bytes no crédito: `413` antes da leitura do JSON, sem consumir a chave, com linha na trilha.
[Collection(LedgerApiDefinition.Name)]
public sealed class BodyLimitTests(LedgerApi api)
{
    [Fact]
    public async Task BodyAboveTheLimitIsRefused()
    {
        var account = await api.OpenAccountAsync();
        var route = $"/v1/accounts/{account.Id}/credits";

        using var atLimit = await api.Client.PostAsync(route, Padded(Credit(), Limit), LedgerTokens.PaymentsHub, NewKey());
        using var overLimit = await api.Client.PostAsync(route, NotJson(Limit + 1), LedgerTokens.PaymentsHub, NewKey());
        using var huge = await api.Client.PostAsync(route, NotJson(30 * 1024 * 1024), LedgerTokens.PaymentsHub, NewKey());
        using var streaming = api.CreateStreamingClient();
        using var chunked = await streaming.PostAsync(route, Padded(Credit(), Limit + 1), LedgerTokens.PaymentsHub, NewKey(), chunked: true);

        await atLimit.ShouldBeCreatedAsync();
        foreach (var refused in new[] { overLimit, huge, chunked })
        {
            var problem = await refused.ShouldBeProblemAsync(413, "payload-too-large");
            problem.GetProperty("title").GetString().ShouldBe("Corpo da requisição grande demais");
        }

        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(1);
    }

    // Sem `Content-Length`, o corpo é lido para medir: o que fica no limite chega inteiro ao endpoint.
    [Fact]
    public async Task ChunkedBodyAtTheLimitReachesTheEndpoint()
    {
        var account = await api.OpenAccountAsync();
        using var streaming = api.CreateStreamingClient();

        using var chunked = await streaming.PostAsync(
            $"/v1/accounts/{account.Id}/credits", Padded(Credit(), Limit), LedgerTokens.PaymentsHub, NewKey(), chunked: true);

        await chunked.ShouldBeCreatedAsync();
        (await api.Rows.EntriesAsync(account.Id)).ShouldHaveSingleItem().Amount.ShouldBe(10.00m);
    }

    [Fact]
    public async Task RefusedBodyDoesNotConsumeTheKey()
    {
        var account = await api.OpenAccountAsync();
        var route = $"/v1/accounts/{account.Id}/credits";
        var key = NewKey();

        using var refused = await api.Client.PostAsync(route, NotJson(Limit + 1), LedgerTokens.PaymentsHub, key);
        using var accepted = await api.Client.PostAsync(route, Padded(Credit(), 100), LedgerTokens.PaymentsHub, key);

        await refused.ShouldBeProblemAsync(413, "payload-too-large");
        await accepted.ShouldBeCreatedAsync();
        accepted.IsReplay().ShouldBeFalse();
    }

    [Fact]
    public async Task RefusedBodyIsAudited()
    {
        var account = await api.OpenAccountAsync();

        var refused = await api.Client.SendTracedAsync(
            api.Completions, HttpMethod.Post, $"/v1/accounts/{account.Id}/credits", body: null, LedgerTokens.PaymentsHub, NewKey(),
            rawJson: NotJson(Limit + 1));

        (refused.Status, refused.ProblemType).ShouldBe((413, "payload-too-large"));
        (await api.Database.CheckedAuditRowAsync("ledger", refused))
            .ShouldBe(AuditRow.Expected("payments-hub", null, "credit", "account", account.Id, 413));
    }

    private static object Credit() => new { amount = "10.00", category = "PIX_IN", externalReference = LedgerClient.NewReference() };
}
