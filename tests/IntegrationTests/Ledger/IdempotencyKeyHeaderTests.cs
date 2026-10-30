using IntegrationTests.Identity;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class IdempotencyKeyHeaderTests(LedgerApi api)
{
    public static TheoryData<string, string?> MissingKeyPerRoute
    {
        get
        {
            var data = new TheoryData<string, string?>();
            foreach (var route in LedgerPosts.Routes)
            {
                data.Add(route, null);
                data.Add(route, string.Empty);
            }

            return data;
        }
    }

    public static TheoryData<string, string> MalformedKeyPerRoute
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var route in LedgerPosts.Routes)
            {
                data.Add(route, "abc");
                data.Add(route, "0f8fad5bd9cb469fa16570867728950e");
            }

            return data;
        }
    }

    public static TheoryData<string> Routes => new(LedgerPosts.Routes);

    [Theory]
    [MemberData(nameof(MissingKeyPerRoute))]
    public async Task MissingKeyIsRejectedOnEveryPost(string route, string? idempotencyKey)
    {
        var (account, entryId) = await api.Client.FundedAccountAsync();
        var (accounts, entries, keys) = await CountsAsync();

        using var response = await api.Client.SendAsync(LedgerPosts.For(route, account, entryId), idempotencyKey);

        await response.ShouldBeProblemAsync(400, "idempotency-key-missing");
        (await CountsAsync()).ShouldBe((accounts, entries, keys));
    }

    [Theory]
    [MemberData(nameof(MalformedKeyPerRoute))]
    public async Task MalformedKeyIsRejected(string route, string idempotencyKey)
    {
        var (account, entryId) = await api.Client.FundedAccountAsync();
        var (accounts, entries, keys) = await CountsAsync();

        using var response = await api.Client.SendAsync(LedgerPosts.For(route, account, entryId), idempotencyKey);

        await response.ShouldBeProblemAsync(400, "idempotency-key-invalid");
        (await CountsAsync()).ShouldBe((accounts, entries, keys));
    }

    [Fact]
    public async Task MalformedKeyIsRejectedAndHyphenatedUuidIsAccepted()
    {
        var (account, entryId) = await api.Client.FundedAccountAsync();

        using var response = await api.Client.SendAsync(LedgerPosts.For("credits", account, entryId), "0f8fad5b-d9cb-469f-a165-70867728950e");

        await response.ShouldBeEntryAsync();
        (await api.Rows.IdempotencyKeysAsync(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"))).Count.ShouldBe(1);
    }

    [Fact]
    public async Task KeyIsCheckedBeforeBodyAndPath()
    {
        var (account, _) = await api.Client.FundedAccountAsync();

        using var invalidBody = await api.Client.SendJsonAsync(
            HttpMethod.Post,
            $"/v1/accounts/{account.Id}/credits",
            new { amount = "abc", category = "PIX_IN", externalReference = LedgerClient.NewReference() },
            LedgerTokens.PaymentsHub,
            idempotencyKey: null);
        using var invalidPath = await api.Client.SendJsonAsync(
            HttpMethod.Post,
            "/v1/accounts/nao-e-uuid/debits",
            new { amount = "1.00", category = "PIX_OUT", externalReference = LedgerClient.NewReference() },
            LedgerTokens.Channel(account.CustomerId),
            idempotencyKey: null);

        await invalidBody.ShouldBeProblemAsync(400, "idempotency-key-missing");
        await invalidPath.ShouldBeProblemAsync(400, "idempotency-key-missing");
    }

    [Fact]
    public async Task CreditLimitDoesNotRequireKey()
    {
        var account = await api.Client.OpenAccountAsync();

        // O cliente de teste nunca põe a chave num `PUT`.
        using var response = await api.Client.SetCreditLimitAsync(account.Id, "100.00");

        ((int)response.StatusCode).ShouldBe(204, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().CreditLimit.ShouldBe(100m);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task AuthorizationPrecedesKeyCheck(string route)
    {
        var (account, entryId) = await api.Client.FundedAccountAsync();
        var post = LedgerPosts.For(route, account, entryId);
        // Token válido de um client sem o escopo desta rota.
        var wrongScope = route == "credits" ? LedgerTokens.Channel(account.CustomerId) : LedgerTokens.PaymentsHub;

        using var anonymous = await api.Client.SendJsonAsync(HttpMethod.Post, post.Path, post.Body, token: null, idempotencyKey: null);
        using var forbidden = await api.Client.SendJsonAsync(HttpMethod.Post, post.Path, post.Body, wrongScope, idempotencyKey: null);

        await anonymous.ShouldBeAuthenticationRequiredAsync();
        await forbidden.ShouldBeInsufficientScopeAsync();
    }

    private async Task<(long Accounts, long Entries, long Keys)> CountsAsync() =>
        (await api.Rows.AccountCountAsync(), await api.Rows.EntryCountAsync(), await api.Rows.IdempotencyKeyCountAsync());
}
