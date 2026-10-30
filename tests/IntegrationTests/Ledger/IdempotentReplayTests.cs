using System.Text.Json.Nodes;
using IntegrationTests.Identity;
using static IntegrationTests.Identity.TestTokenIssuer;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class IdempotentReplayTests(LedgerApi api)
{
    public static TheoryData<string> Routes => new(LedgerPosts.Routes);

    public static TheoryData<string> Rejections => new(
        "account-not-found", "entry-not-found", "entry-already-reversed", "duplicate-external-reference", "insufficient-funds", "entry-is-reversal");

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task RepeatedRequestReturnsOriginalResponse(string route)
    {
        var (account, entryId) = await api.Client.FundedAccountAsync();
        var post = LedgerPosts.For(route, account, entryId);
        var key = Guid.NewGuid().ToString();
        var accountsBefore = await api.Rows.AccountCountAsync();
        var entriesBefore = await api.Rows.EntryCountAsync();

        using var first = await api.Client.SendAsync(post, key);
        using var second = await api.Client.SendAsync(post, key);
        using var third = await api.Client.SendAsync(post, key);

        var firstBody = await first.ShouldBeCreatedAsync();
        var secondBody = await second.ShouldBeCreatedAsync();
        var thirdBody = await third.ShouldBeCreatedAsync();
        first.IsReplay().ShouldBeFalse();
        first.Headers.Contains(LedgerClient.ReplayedHeader).ShouldBeFalse();
        second.IsReplay().ShouldBeTrue();
        third.IsReplay().ShouldBeTrue();
        JsonNode.DeepEquals(firstBody, secondBody).ShouldBeTrue($"{firstBody.ToJsonString()} vs {secondBody.ToJsonString()}");
        JsonNode.DeepEquals(firstBody, thirdBody).ShouldBeTrue($"{firstBody.ToJsonString()} vs {thirdBody.ToJsonString()}");
        (await api.Rows.AccountCountAsync()).ShouldBe(route == "opening" ? accountsBefore + 1 : accountsBefore);
        (await api.Rows.EntryCountAsync()).ShouldBe(route == "opening" ? entriesBefore : entriesBefore + 1);
        if (route == "debits")
        {
            (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().Balance.ShouldBe(90m);
        }
    }

    [Fact]
    public async Task SameMeaningIsReplayed()
    {
        var (account, _) = await api.Client.FundedAccountAsync();
        var reference = LedgerClient.NewReference();
        var key = Guid.NewGuid().ToString();
        var path = $"/v1/accounts/{account.Id}/credits";
        var entriesBefore = (await api.Rows.EntriesAsync(account.Id)).Count;

        using var original = await Post($$"""{ "amount": "10.0", "category": "PIX_IN", "externalReference": "{{reference}}" }""");
        using var twoDecimals = await Post($$"""{ "amount": "10.00", "category": "PIX_IN", "externalReference": "{{reference}}" }""");
        using var reordered = await Post($$"""{ "externalReference": "{{reference}}", "category": "PIX_IN", "amount": "10.00" }""");
        using var unknownField = await Post($$"""{ "amount": "10.00", "category": "PIX_IN", "externalReference": "{{reference}}", "note": "x" }""");

        var id = (await original.ShouldBeEntryAsync()).GetProperty("id").GetGuid();
        original.IsReplay().ShouldBeFalse();
        foreach (var repeated in new[] { twoDecimals, reordered, unknownField })
        {
            (await repeated.ShouldBeEntryAsync()).GetProperty("id").GetGuid().ShouldBe(id);
            repeated.IsReplay().ShouldBeTrue();
        }

        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(entriesBefore + 1);

        Task<HttpResponseMessage> Post(string json) => api.Client.SendRawAsync(HttpMethod.Post, path, json, LedgerTokens.PaymentsHub, key);
    }

    [Fact]
    public async Task ReusedKeyWithDifferentContentIsRejected()
    {
        var (account, _) = await api.Client.FundedAccountAsync();
        var (otherAccount, _) = await api.Client.FundedAccountAsync();
        var reference = LedgerClient.NewReference();
        var key = Guid.NewGuid().ToString();
        // Um client com os dois escopos: só a operação difere entre o crédito e o débito.
        var creditAndDebit = Sign(Service("payments-hub", "ledger.credits.write ledger.debits.write"));
        // Débito exige categoria de débito; valor e referência são os do crédito.
        var body = new { amount = "10.00", category = "PIX_OUT", externalReference = reference };
        using var original = await api.Client.CreditAsync(account.Id, "10.00", "PIX_IN", reference, idempotencyKey: key);
        await original.ShouldBeEntryAsync();
        var entriesBefore = await api.Rows.EntryCountAsync();

        using var otherAmount = await api.Client.CreditAsync(account.Id, "20.00", "PIX_IN", reference, idempotencyKey: key);
        using var otherOperation = await api.Client.SendJsonAsync(
            HttpMethod.Post, $"/v1/accounts/{account.Id}/debits", body, creditAndDebit, key);
        using var otherAccountCredit = await api.Client.CreditAsync(otherAccount.Id, "10.00", "PIX_IN", reference, idempotencyKey: key);

        await otherAmount.ShouldBeProblemAsync(422, "idempotency-key-reused");
        await otherOperation.ShouldBeProblemAsync(422, "idempotency-key-reused");
        await otherAccountCredit.ShouldBeProblemAsync(422, "idempotency-key-reused");
        (await api.Rows.EntryCountAsync()).ShouldBe(entriesBefore);
    }

    [Fact]
    public async Task ReusedKeyWithDifferentContentIsRejectedForAnotherCustomer()
    {
        var (account, _) = await api.Client.FundedAccountAsync();
        var key = Guid.NewGuid().ToString();
        var body = new { amount = "10.00", category = "PIX_OUT", externalReference = LedgerClient.NewReference() };
        var path = $"/v1/accounts/{account.Id}/debits";
        using var owner = await api.Client.SendJsonAsync(HttpMethod.Post, path, body, LedgerTokens.Channel(account.CustomerId), key);
        await owner.ShouldBeEntryAsync();
        var entriesBefore = await api.Rows.EntryCountAsync();

        // Outro cliente do `channel-app` repete chave e corpo: sem o titular no hash, receberia o lançamento alheio.
        using var other = await api.Client.SendJsonAsync(HttpMethod.Post, path, body, LedgerTokens.Channel(Guid.NewGuid()), key);

        var problem = await other.ShouldBeProblemAsync(422, "idempotency-key-reused");
        problem.TryGetProperty("id", out _).ShouldBeFalse();
        (await api.Rows.EntryCountAsync()).ShouldBe(entriesBefore);
    }

    [Fact]
    public async Task KeyIsScopedByClient()
    {
        var account = await api.Client.OpenAccountAsync();
        var key = Guid.NewGuid();

        using var credit = await api.Client.CreditAsync(account.Id, "100.00", idempotencyKey: key.ToString());
        var creditId = (await credit.ShouldBeEntryAsync()).GetProperty("id").GetGuid();
        using var reversal = await api.Client.ReverseAsync(account.Id, creditId, idempotencyKey: key.ToString());
        await reversal.ShouldBeEntryAsync();

        credit.IsReplay().ShouldBeFalse();
        reversal.IsReplay().ShouldBeFalse();
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(2);
        (await api.Rows.IdempotencyKeysAsync(key)).Select(row => row.ClientId).ShouldBe(["backoffice", "payments-hub"]);
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task BusinessRejectionIsReplayed(string type)
    {
        var (account, creditId) = await api.Client.FundedAccountAsync();
        var key = Guid.NewGuid();
        var (status, send, changeState) = await RejectionAsync(type, account, creditId, key.ToString());

        using var first = await send();
        await first.ShouldBeProblemAsync(status, type);
        await changeState();
        var entriesBefore = await api.Rows.EntryCountAsync();
        using var repeated = await send();

        await repeated.ShouldBeProblemAsync(status, type);
        first.IsReplay().ShouldBeFalse();
        repeated.IsReplay().ShouldBeTrue();
        JsonNode.DeepEquals(await first.BodyWithoutTraceAsync(), await repeated.BodyWithoutTraceAsync()).ShouldBeTrue();
        (await api.Rows.EntryCountAsync()).ShouldBe(entriesBefore);
        (await api.Rows.IdempotencyKeysAsync(key)).ShouldHaveSingleItem().ResponseStatus.ShouldBe(status);
    }

    [Fact]
    public async Task ValidationErrorDoesNotConsumeKey()
    {
        var account = await api.Client.OpenAccountAsync();
        var key = Guid.NewGuid();

        using var invalid = await api.Client.CreditAsync(account.Id, "abc", idempotencyKey: key.ToString());
        await invalid.ShouldBeValidationErrorAsync("amount");
        var keysAfterInvalid = await api.Rows.IdempotencyKeysAsync(key);
        using var valid = await api.Client.CreditAsync(account.Id, "10.00", idempotencyKey: key.ToString());

        keysAfterInvalid.ShouldBeEmpty();
        await valid.ShouldBeEntryAsync();
        valid.IsReplay().ShouldBeFalse();
    }

    // Cada recusa com o envio que a provoca e uma mudança de estado que, sem a chave, mudaria o resultado.
    private async Task<(int Status, Func<Task<HttpResponseMessage>> Send, Func<Task> ChangeState)> RejectionAsync(
        string type, TestAccount account, Guid creditId, string key)
    {
        // Mesma referência nas duas tentativas: a repetição tem o mesmo conteúdo.
        var reference = LedgerClient.NewReference();
        switch (type)
        {
            case "account-not-found":
                var missingAccount = Guid.NewGuid();
                return (404, () => api.Client.CreditAsync(missingAccount, "10.00", externalReference: reference, idempotencyKey: key), () => api.Database.ExecuteAsync(
                    "postgres", $"INSERT INTO ledger.accounts (id, customer_id, created_at) VALUES ('{missingAccount}', '{Guid.NewGuid()}', now())"));
            case "entry-not-found":
                var missingEntry = Guid.NewGuid();
                return (404, () => api.Client.ReverseAsync(account.Id, missingEntry, reference, key), () => api.Client.CreditOkAsync(account.Id, "1.00"));
            case "entry-already-reversed":
                using (var reversal = await api.Client.ReverseAsync(account.Id, creditId))
                {
                    await reversal.ShouldBeEntryAsync();
                }

                return (409, () => api.Client.ReverseAsync(account.Id, creditId, reference, key), () => api.Client.CreditOkAsync(account.Id, "1.00"));
            case "duplicate-external-reference":
                using (var original = await api.Client.CreditAsync(account.Id, "1.00", externalReference: reference))
                {
                    await original.ShouldBeEntryAsync();
                }

                return (409, () => api.Client.CreditAsync(account.Id, "1.00", externalReference: reference, idempotencyKey: key),
                    () => api.Client.CreditOkAsync(account.Id, "1.00"));
            case "insufficient-funds":
                // Saldo 100.00: o débito de 150.00 é recusado e, depois do crédito, caberia.
                return (422, () => api.Client.DebitAsync(account, "150.00", externalReference: reference, idempotencyKey: key), () => api.Client.CreditOkAsync(account.Id, "100.00"));
            default:
                using (var reversal = await api.Client.ReverseAsync(account.Id, creditId))
                {
                    var reversalId = (await reversal.ShouldBeEntryAsync()).GetProperty("id").GetGuid();
                    return (422, () => api.Client.ReverseAsync(account.Id, reversalId, reference, key), () => api.Client.CreditOkAsync(account.Id, "1.00"));
                }
        }
    }
}
