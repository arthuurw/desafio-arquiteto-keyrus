using IntegrationTests.Infrastructure;
using static IntegrationTests.Infrastructure.AuditRows;

namespace IntegrationTests.Ledger;

// Um comando da escrita que passa de 3 s no banco desfaz a transação e responde `503` `database-timeout` com
// `Retry-After`, sem consumir a chave de idempotência (ADR-0021, ADR-0037).
[Collection(LedgerApiDefinition.Name)]
public sealed class StatementTimeoutTests(LedgerApi api)
{
    // Acima dos 3 s do `statement_timeout`: o trigger segura o comando até o banco cancelá-lo.
    private const int SlowSeconds = 4;

    [Fact]
    public async Task SlowCreditIsRefusedAndTheKeyStaysFree()
    {
        var account = await api.OpenAccountAsync();
        var credits = $"/v1/accounts/{account.Id}/credits";
        var key = NewKey();
        var body = new { amount = "10.00", category = "PIX_IN", externalReference = LedgerClient.NewReference() };

        TracedResponse slow;
        await using (await TestTrigger.SleepAsync(api.Database, "ledger.entries", "INSERT", $"NEW.account_id = '{account.Id}'", SlowSeconds))
        {
            slow = await SendAsync(HttpMethod.Post, credits, body, LedgerTokens.PaymentsHub, key);
        }

        var entries = await api.Rows.EntriesAsync(account.Id);
        var keys = await api.Rows.IdempotencyKeysAsync(Guid.Parse(key));
        var retried = await SendAsync(HttpMethod.Post, credits, body, LedgerTokens.PaymentsHub, key);

        (slow.Status, slow.ProblemType).ShouldBe((503, "database-timeout"), slow.Body);
        slow.Headers["Retry-After"].ShouldBe("1");
        entries.ShouldBeEmpty();
        keys.ShouldBeEmpty();
        (await api.Database.CheckedAuditRowAsync("ledger", slow))
            .ShouldBe(AuditRow.Expected("payments-hub", null, "credit", "account", account.Id, 503));
        (retried.Status, retried.Replayed).ShouldBe((201, false), retried.Body);
        (await api.Rows.EntriesAsync(account.Id)).ShouldHaveSingleItem().Amount.ShouldBe(10.00m);
    }

    // A troca de limite tem transação própria, fora do pipeline de idempotência, e recusa do mesmo jeito.
    [Fact]
    public async Task SlowLimitChangeIsRefused()
    {
        var account = await api.OpenAccountAsync();
        var route = $"/v1/accounts/{account.Id}/credit-limit";

        TracedResponse slow;
        await using (await TestTrigger.SleepAsync(api.Database, "ledger.accounts", "UPDATE", $"NEW.id = '{account.Id}'", SlowSeconds))
        {
            slow = await SendAsync(HttpMethod.Put, route, new { creditLimit = "300.00" }, LedgerTokens.Backoffice);
        }

        (slow.Status, slow.ProblemType).ShouldBe((503, "database-timeout"), slow.Body);
        slow.Headers["Retry-After"].ShouldBe("1");
        (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().CreditLimit.ShouldBe(0m);
        (await api.Database.CheckedAuditRowAsync("ledger", slow))
            .ShouldBe(AuditRow.Expected("backoffice", null, "change-credit-limit", "account", account.Id, 503));
    }

    private Task<TracedResponse> SendAsync(HttpMethod method, string route, object body, string token, string? idempotencyKey = null) =>
        api.Client.SendTracedAsync(api.Completions, method, route, body, token, idempotencyKey);
}
