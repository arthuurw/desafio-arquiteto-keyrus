using IntegrationTests.Identity;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class IdempotencyKeyHeaderTests(LedgerApi api)
{
    // Sem o header, ou com ele vazio.
    public static TheoryData<LedgerRoute, bool> MissingKeyPerRoute
    {
        get
        {
            var data = new TheoryData<LedgerRoute, bool>();
            foreach (var route in LedgerPosts.Routes)
            {
                data.Add(route, false);
                data.Add(route, true);
            }

            return data;
        }
    }

    public static TheoryData<LedgerRoute, string> MalformedKeyPerRoute
    {
        get
        {
            var data = new TheoryData<LedgerRoute, string>();
            foreach (var route in LedgerPosts.Routes)
            {
                data.Add(route, "abc");
                data.Add(route, "0f8fad5bd9cb469fa16570867728950e");
            }

            return data;
        }
    }

    public static TheoryData<LedgerRoute> Routes => new(LedgerPosts.Routes);

    [Theory]
    [MemberData(nameof(MissingKeyPerRoute))]
    public async Task MissingKeyIsRejectedOnEveryPost(LedgerRoute route, bool emptyHeader)
    {
        var (account, entryId) = await api.Client.FundedAccountAsync();
        var (accounts, entries, keys) = await CountsAsync();

        using var response = await api.Client.SendAsync(
            LedgerPosts.For(route, account, entryId), emptyHeader ? KeyHeader.FromString(string.Empty) : KeyHeader.None);

        await response.ShouldBeProblemAsync(400, "idempotency-key-missing");
        (await CountsAsync()).ShouldBe((accounts, entries, keys));
    }

    [Theory]
    [MemberData(nameof(MalformedKeyPerRoute))]
    public async Task MalformedKeyIsRejected(LedgerRoute route, string idempotencyKey)
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

        using var response = await api.Client.SendAsync(LedgerPosts.For(LedgerRoute.Credits, account, entryId), "0f8fad5b-d9cb-469f-a165-70867728950e");

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
            idempotencyKey: KeyHeader.None);
        using var invalidPath = await api.Client.SendJsonAsync(
            HttpMethod.Post,
            "/v1/accounts/nao-e-uuid/debits",
            new { amount = "1.00", category = "PIX_OUT", externalReference = LedgerClient.NewReference() },
            LedgerTokens.Channel(account.CustomerId),
            idempotencyKey: KeyHeader.None);

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
    public async Task AuthorizationPrecedesKeyCheck(LedgerRoute route)
    {
        var (account, entryId) = await api.Client.FundedAccountAsync();
        var post = LedgerPosts.For(route, account, entryId);
        // Token válido de um client sem o escopo desta rota.
        var wrongScope = route == LedgerRoute.Credits ? LedgerTokens.Channel(account.CustomerId) : LedgerTokens.PaymentsHub;

        using var anonymous = await api.Client.SendJsonAsync(HttpMethod.Post, post.Path, post.Body, token: null, idempotencyKey: KeyHeader.None);
        using var forbidden = await api.Client.SendJsonAsync(HttpMethod.Post, post.Path, post.Body, wrongScope, idempotencyKey: KeyHeader.None);

        await anonymous.ShouldBeAuthenticationRequiredAsync();
        await forbidden.ShouldBeInsufficientScopeAsync();
    }

    private async Task<(long Accounts, long Entries, long Keys)> CountsAsync() =>
        (await api.Rows.AccountCountAsync(), await api.Rows.EntryCountAsync(), await api.Rows.IdempotencyKeyCountAsync());
}
