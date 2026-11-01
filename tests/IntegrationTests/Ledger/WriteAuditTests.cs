using IntegrationTests.Identity;
using IntegrationTests.Infrastructure;
using static IntegrationTests.Infrastructure.AuditRows;
using Microsoft.Extensions.Logging;

namespace IntegrationTests.Ledger;

// Toda requisição a uma rota de escrita do Ledger deixa uma linha em `ledger.audit_events`, com o desfecho devolvido.
[Collection(LedgerApiDefinition.Name)]
public sealed class WriteAuditTests(LedgerApi api)
{
    [Fact]
    public async Task ConfirmedWritesRecordOneRowEach()
    {
        var customer = Guid.NewGuid();
        await api.Database.SeedCustomerAsync(customer);

        var opened = await SendAsync(HttpMethod.Post, "/v1/accounts", new { customerId = customer }, LedgerTokens.Backoffice, NewKey());
        opened.Status.ShouldBe(201, opened.Body);
        var account = new TestAccount(opened.CreatedId, customer);
        var credit = await SendAsync(HttpMethod.Post, $"/v1/accounts/{account.Id}/credits", Entry("100.00", "PIX_IN"), LedgerTokens.PaymentsHub, NewKey());
        var debit = await SendAsync(HttpMethod.Post, $"/v1/accounts/{account.Id}/debits", Entry("30.00", "PIX_OUT"), LedgerTokens.Channel(customer), NewKey());
        var creditId = credit.CreatedId;
        // O limite antes do estorno: estornar os 100.00 com 70.00 de saldo usa 30.00 dele.
        var limit = await SendAsync(HttpMethod.Put, $"/v1/accounts/{account.Id}/credit-limit", new { creditLimit = "500.00" }, LedgerTokens.Backoffice);
        var reversal = await SendAsync(
            HttpMethod.Post, $"/v1/accounts/{account.Id}/entries/{creditId}/reversal", new { externalReference = LedgerClient.NewReference() },
            LedgerTokens.Backoffice, NewKey());

        credit.Status.ShouldBe(201, credit.Body);
        debit.Status.ShouldBe(201, debit.Body);
        reversal.Status.ShouldBe(201, reversal.Body);
        limit.Status.ShouldBe(204, limit.Body);

        (await RowAsync(opened)).ShouldBe(Row("backoffice", null, "open-account", "account", account.Id, 201));
        (await RowAsync(credit)).ShouldBe(Row("payments-hub", null, "credit", "account", account.Id, 201));
        (await RowAsync(debit)).ShouldBe(Row("channel-app", customer, "debit", "account", account.Id, 201));
        (await RowAsync(reversal)).ShouldBe(Row("backoffice", null, "reverse-entry", "entry", creditId, 201));
        (await RowAsync(limit)).ShouldBe(Row("backoffice", null, "change-credit-limit", "account", account.Id, 204));
    }

    [Fact]
    public async Task RefusalsAndReplaysRecordTheReturnedStatus()
    {
        var account = await api.OpenAccountAsync();
        var missing = Guid.NewGuid();
        var credits = $"/v1/accounts/{account.Id}/credits";

        var overLimit = await SendAsync(HttpMethod.Post, $"/v1/accounts/{account.Id}/debits", Entry("10.00", "PIX_OUT"), LedgerTokens.Channel(account.CustomerId), NewKey());
        var noAmount = await SendAsync(HttpMethod.Post, credits, new { category = "PIX_IN", externalReference = LedgerClient.NewReference() }, LedgerTokens.PaymentsHub, NewKey());
        var invalidId = await SendAsync(HttpMethod.Post, "/v1/accounts/abc/credits", Entry("1.00", "PIX_IN"), LedgerTokens.PaymentsHub, NewKey());
        var unknown = await SendAsync(HttpMethod.Post, $"/v1/accounts/{missing}/credits", Entry("1.00", "PIX_IN"), LedgerTokens.PaymentsHub, NewKey());

        var key = NewKey();
        var body = Entry("5.00", "PIX_IN");
        var first = await SendAsync(HttpMethod.Post, credits, body, LedgerTokens.PaymentsHub, key);
        var replay = await SendAsync(HttpMethod.Post, credits, body, LedgerTokens.PaymentsHub, key);
        var reused = await SendAsync(HttpMethod.Post, credits, Entry("6.00", "PIX_IN"), LedgerTokens.PaymentsHub, key);

        await api.Client.SetCreditLimitOkAsync(account.Id, "100.00");
        await api.Client.DebitOkAsync(account, "50.00");
        var belowUsed = await SendAsync(HttpMethod.Put, $"/v1/accounts/{account.Id}/credit-limit", new { creditLimit = "10.00" }, LedgerTokens.Backoffice);

        TracedResponse busy;
        await using (await AccountLockHolder.HoldAsync(api.Database, account.Id))
        {
            busy = await SendAsync(HttpMethod.Post, $"/v1/accounts/{account.Id}/debits", Entry("1.00", "PIX_OUT"), LedgerTokens.Channel(account.CustomerId), NewKey());
        }

        (overLimit.Status, overLimit.ProblemType).ShouldBe((422, "insufficient-funds"));
        (await RowAsync(overLimit)).ShouldBe(Row("channel-app", account.CustomerId, "debit", "account", account.Id, 422));
        (noAmount.Status, noAmount.ProblemType).ShouldBe((400, "validation-error"));
        (await RowAsync(noAmount)).ShouldBe(Row("payments-hub", null, "credit", "account", account.Id, 400));
        (invalidId.Status, invalidId.ProblemType).ShouldBe((404, "account-not-found"));
        (await RowAsync(invalidId)).ShouldBe(Row("payments-hub", null, "credit", "account", null, 404));
        (unknown.Status, unknown.ProblemType).ShouldBe((404, "account-not-found"));
        (await RowAsync(unknown)).ShouldBe(Row("payments-hub", null, "credit", "account", missing, 404));
        first.Status.ShouldBe(201, first.Body);
        (await RowAsync(first)).Result.ShouldBe(201);
        (replay.Status, replay.Replayed).ShouldBe((201, true));
        (await RowAsync(replay)).ShouldBe(Row("payments-hub", null, "credit", "account", account.Id, 201));
        (reused.Status, reused.ProblemType).ShouldBe((422, "idempotency-key-reused"));
        (await RowAsync(reused)).ShouldBe(Row("payments-hub", null, "credit", "account", account.Id, 422));
        (belowUsed.Status, belowUsed.ProblemType).ShouldBe((422, "credit-limit-below-balance"));
        (await RowAsync(belowUsed)).ShouldBe(Row("backoffice", null, "change-credit-limit", "account", account.Id, 422));
        (busy.Status, busy.ProblemType).ShouldBe((409, "concurrency-timeout"));
        (await RowAsync(busy)).ShouldBe(Row("channel-app", account.CustomerId, "debit", "account", account.Id, 409));
    }

    [Fact]
    public async Task AuthenticationAndScopeRefusalsAreRecorded()
    {
        var account = await api.OpenAccountAsync();
        var credits = $"/v1/accounts/{account.Id}/credits";

        var anonymous = await SendAsync(HttpMethod.Post, credits, Entry("1.00", "PIX_IN"), token: null, NewKey());
        var forged = await SendAsync(
            HttpMethod.Post, credits, Entry("1.00", "PIX_IN"),
            TestTokenIssuer.SignWithOtherKey(TestTokenIssuer.Service("payments-hub", "ledger.credits.write")), NewKey());
        var withoutScope = await SendAsync(HttpMethod.Post, $"/v1/accounts/{account.Id}/debits", Entry("1.00", "PIX_OUT"), LedgerTokens.PaymentsHub, NewKey());

        (anonymous.Status, anonymous.ProblemType).ShouldBe((401, "authentication-required"));
        (await RowAsync(anonymous)).ShouldBe(Row(null, null, "credit", "account", account.Id, 401));
        (forged.Status, forged.ProblemType).ShouldBe((401, "authentication-required"));
        (await RowAsync(forged)).ShouldBe(Row(null, null, "credit", "account", account.Id, 401));
        (withoutScope.Status, withoutScope.ProblemType).ShouldBe((403, "insufficient-scope"));
        (await RowAsync(withoutScope)).ShouldBe(Row("payments-hub", null, "debit", "account", account.Id, 403));
    }

    [Fact]
    public async Task UnhandledExceptionRecords500()
    {
        var account = await api.OpenAccountAsync();

        TracedResponse failed;
        await using (await TestTrigger.CreateAsync(api.Database, "ledger.entries", "INSERT", $"NEW.account_id = '{account.Id}'"))
        {
            failed = await SendAsync(HttpMethod.Post, $"/v1/accounts/{account.Id}/credits", Entry("1.00", "PIX_IN"), LedgerTokens.PaymentsHub, NewKey());
        }

        (failed.Status, failed.ProblemType).ShouldBe((500, "internal-error"));
        (await RowAsync(failed)).ShouldBe(Row("payments-hub", null, "credit", "account", account.Id, 500));
    }

    [Fact]
    public async Task AbandonedWriteRecords499()
    {
        var account = await api.OpenAccountAsync();
        var trace = AuditRows.NewTrace();

        await using (await AccountLockHolder.HoldAsync(api.Database, account.Id))
        {
            using var request = AuditRows.TracedRequest(
                HttpMethod.Post, $"/v1/accounts/{account.Id}/debits", System.Text.Json.JsonSerializer.Serialize(Entry("1.00", "PIX_OUT")),
                LedgerTokens.Channel(account.CustomerId), NewKey(), trace);
            using var abandon = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

            // O débito para no lock da conta; o cliente desiste enquanto ele espera, antes do `lock_timeout`.
            var sending = api.Client.SendAsync(request, abandon.Token);
            await api.Database.WaitForBlockedRequestsAsync(1);
            await abandon.CancelAsync();

            await Should.ThrowAsync<OperationCanceledException>(sending);
            await api.Completions.WaitAsync(trace);
        }

        var row = (await api.Database.AuditRowsAsync("ledger", trace)).ShouldHaveSingleItem();
        row.ShouldBe(new AuditRow("channel-app", account.CustomerId, "debit", "account", account.Id, 499, row.TraceId));
        row.TraceId.ShouldStartWith($"00-{trace}-");
    }

    [Fact]
    public async Task FailedAuditUndoesTheWrite()
    {
        var account = await api.OpenAccountAsync();
        await api.Client.CreditOkAsync(account.Id, "10.00");
        var before = await api.Rows.AccountAsync(account.Id);
        var entriesBefore = (await api.Rows.EntriesAsync(account.Id)).Count;
        var key = NewKey();
        var body = Entry("25.00", "PIX_IN");

        TracedResponse refused;
        await using (await api.Database.RefuseAuditInsertAsync("ledger", account.Id))
        {
            refused = await SendAsync(HttpMethod.Post, $"/v1/accounts/{account.Id}/credits", body, LedgerTokens.PaymentsHub, key);
        }

        var after = await api.Rows.AccountAsync(account.Id);
        var entriesAfter = (await api.Rows.EntriesAsync(account.Id)).Count;
        var keys = await api.Rows.IdempotencyKeysAsync(Guid.Parse(key));
        var retried = await SendAsync(HttpMethod.Post, $"/v1/accounts/{account.Id}/credits", body, LedgerTokens.PaymentsHub, key);

        (refused.Status, refused.ProblemType).ShouldBe((500, "internal-error"));
        after.ShouldBe(before);
        entriesAfter.ShouldBe(entriesBefore);
        keys.ShouldBeEmpty();
        (await api.Database.AuditRowsAsync("ledger", refused.Trace)).ShouldBeEmpty();
        (retried.Status, retried.Replayed).ShouldBe((201, false), retried.Body);
        (await RowAsync(retried)).ShouldBe(Row("payments-hub", null, "credit", "account", account.Id, 201));
    }

    [Fact]
    public async Task FailedAuditUndoesTheLimitChange()
    {
        var account = await api.OpenAccountAsync();
        var route = $"/v1/accounts/{account.Id}/credit-limit";
        var before = await api.Rows.AccountAsync(account.Id);

        TracedResponse refused;
        await using (await api.Database.RefuseAuditInsertAsync("ledger", account.Id))
        {
            refused = await SendAsync(HttpMethod.Put, route, new { creditLimit = "300.00" }, LedgerTokens.Backoffice);
        }

        var after = await api.Rows.AccountAsync(account.Id);
        var retried = await SendAsync(HttpMethod.Put, route, new { creditLimit = "300.00" }, LedgerTokens.Backoffice);

        (refused.Status, refused.ProblemType).ShouldBe((500, "internal-error"));
        after.ShouldBe(before);
        after.ShouldNotBeNull().CreditLimit.ShouldBe(0m);
        (await api.Database.AuditRowsAsync("ledger", refused.Trace)).ShouldBeEmpty();
        retried.Status.ShouldBe(204, retried.Body);
        (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().CreditLimit.ShouldBe(300m);
        (await RowAsync(retried)).ShouldBe(Row("backoffice", null, "change-credit-limit", "account", account.Id, 204));
    }

    [Fact]
    public async Task FailedAuditKeepsTheRefusal()
    {
        var account = await api.OpenAccountAsync();
        var debits = $"/v1/accounts/{account.Id}/debits";
        var reference = LedgerClient.NewReference();
        var mark = api.ApiLog.Mark();

        TracedResponse overLimit, anonymous, withoutScope;
        await using (await api.Database.RefuseAuditInsertAsync("ledger", account.Id))
        {
            overLimit = await SendAsync(
                HttpMethod.Post, debits, new { amount = "10.00", category = "PIX_OUT", externalReference = reference }, LedgerTokens.Channel(account.CustomerId), NewKey());
            anonymous = await SendAsync(HttpMethod.Post, debits, Entry("1.00", "PIX_OUT"), token: null, NewKey());
            withoutScope = await SendAsync(HttpMethod.Post, debits, Entry("1.00", "PIX_OUT"), LedgerTokens.PaymentsHub, NewKey());
        }

        var errors = api.ApiLog.Since(mark).Where(line => line.Level == LogLevel.Error).ToList();

        (overLimit.Status, overLimit.ProblemType).ShouldBe((422, "insufficient-funds"));
        (anonymous.Status, anonymous.ProblemType).ShouldBe((401, "authentication-required"));
        (withoutScope.Status, withoutScope.ProblemType).ShouldBe((403, "insufficient-scope"));
        foreach (var refusal in new[] { overLimit, anonymous, withoutScope })
        {
            var line = errors.Where(error => error.Message.Contains(refusal.BodyTraceId!, StringComparison.Ordinal)).ShouldHaveSingleItem();
            line.Message.ShouldContain("debit");
            line.Message.ShouldContain($"{refusal.Status}");
            line.Message.ShouldNotContain(reference);
            line.Message.ShouldNotContain("10.00");
            (await api.Database.AuditRowsAsync("ledger", refusal.Trace)).ShouldBeEmpty();
        }
    }

    private Task<TracedResponse> SendAsync(HttpMethod method, string route, object body, string? token, string? idempotencyKey = null) =>
        api.Client.SendTracedAsync(api.Completions, method, route, body, token, idempotencyKey);

    private Task<AuditRow> RowAsync(TracedResponse response) => api.Database.CheckedAuditRowAsync("ledger", response);

    private static AuditRow Row(string? client, Guid? callerCustomer, string operation, string resourceType, Guid? resource, int result) =>
        AuditRow.Expected(client, callerCustomer, operation, resourceType, resource, result);

    private static object Entry(string amount, string category) => new { amount, category, externalReference = LedgerClient.NewReference() };
}
