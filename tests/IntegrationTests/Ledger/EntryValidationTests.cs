namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class EntryValidationTests(LedgerApi api)
{
    private static readonly string[] EntryRoutes = ["credits", "debits"];
    private static readonly string[] AllRoutes = ["credits", "debits", "reversal"];
    private static readonly string[] CreditCategories = ["PIX_IN", "TED_IN", "BOLETO_IN", "DEPOSIT", "ADJUSTMENT_IN"];
    private static readonly string[] DebitCategories = ["PIX_OUT", "TED_OUT", "BOLETO_OUT", "CARD", "FEE", "ADJUSTMENT_OUT"];
    private static readonly string ReferenceOf64 = "Ab0._:-" + new string('x', 57);

    // `amount` em JSON cru, para `10.5` chegar como número JSON e provar que só string é aceita.
    public static TheoryData<string, string> InvalidAmounts
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var route in EntryRoutes)
            {
                foreach (var amount in new[] { "\"0.00\"", "\"-1.00\"", "\"10.001\"", "\"10.100\"", "10.5", "\"abc\"", "\"1000000000000.00\"" })
                {
                    data.Add(route, amount);
                }
            }

            return data;
        }
    }

    public static TheoryData<string, string, string> ValidAmounts
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var route in EntryRoutes)
            {
                data.Add(route, "\"999999999999.99\"", "999999999999.99");
                data.Add(route, "\"10.1\"", "10.10");
            }

            return data;
        }
    }

    public static TheoryData<string, string> MatchingCategories
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var category in CreditCategories)
            {
                data.Add("credits", category);
            }

            foreach (var category in DebitCategories)
            {
                data.Add("debits", category);
            }

            return data;
        }
    }

    public static TheoryData<string, string> MismatchedCategories
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var category in CreditCategories)
            {
                data.Add("debits", category);
            }

            foreach (var category in DebitCategories)
            {
                data.Add("credits", category);
            }

            foreach (var category in new[] { "REVERSAL", "pix_in" })
            {
                data.Add("credits", category);
                data.Add("debits", category);
            }

            return data;
        }
    }

    public static TheoryData<string, string?> InvalidReferences
    {
        get
        {
            var data = new TheoryData<string, string?>();
            foreach (var route in AllRoutes)
            {
                data.Add(route, null);
                data.Add(route, "");
                data.Add(route, new string('A', 65));
                data.Add(route, "joao silva");
            }

            return data;
        }
    }

    public static TheoryData<string> Routes => new(AllRoutes);

    [Theory]
    [MemberData(nameof(InvalidAmounts))]
    public async Task AmountMustBePositiveWithTwoDecimals(string route, string amount)
    {
        var account = await FundedAccountAsync();
        var before = await api.Rows.EntriesAsync(account.Id);

        using var response = await PostAsync(account, route, $$"""{ "amount": {{amount}}, "category": "{{CategoryFor(route)}}", "externalReference": "{{LedgerClient.NewReference()}}" }""");

        await response.ShouldBeValidationErrorAsync("amount");
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(before.Count);
    }

    [Theory]
    [MemberData(nameof(ValidAmounts))]
    public async Task AmountMustBePositiveWithTwoDecimalsAndValidOnesAreStored(string route, string amount, string storedAmount)
    {
        var account = await FundedAccountAsync();
        var before = await api.Rows.EntriesAsync(account.Id);

        using var response = await PostAsync(account, route, $$"""{ "amount": {{amount}}, "category": "{{CategoryFor(route)}}", "externalReference": "{{LedgerClient.NewReference()}}" }""");

        (await response.ShouldBeEntryAsync()).GetProperty("amount").GetString().ShouldBe(storedAmount);
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(before.Count + 1);
    }

    [Theory]
    [MemberData(nameof(MismatchedCategories))]
    public async Task CategoryMustMatchEntryType(string route, string category)
    {
        var account = await FundedAccountAsync();
        var before = await api.Rows.EntriesAsync(account.Id);

        using var response = await PostAsync(account, route, $$"""{ "amount": "1.00", "category": "{{category}}", "externalReference": "{{LedgerClient.NewReference()}}" }""");

        await response.ShouldBeValidationErrorAsync("category");
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(before.Count);
    }

    [Theory]
    [MemberData(nameof(MatchingCategories))]
    public async Task CategoryMustMatchEntryTypeAndMatchingOnesAreStored(string route, string category)
    {
        var account = await FundedAccountAsync();
        var before = await api.Rows.EntriesAsync(account.Id);

        using var response = await PostAsync(account, route, $$"""{ "amount": "1.00", "category": "{{category}}", "externalReference": "{{LedgerClient.NewReference()}}" }""");

        (await response.ShouldBeEntryAsync()).GetProperty("category").GetString().ShouldBe(category);
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(before.Count + 1);
    }

    [Theory]
    [MemberData(nameof(InvalidReferences))]
    public async Task ExternalReferenceMustMatchFormat(string route, string? externalReference)
    {
        var account = await FundedAccountAsync();
        var entryId = (await api.Client.CreditOkAsync(account.Id, "1.00")).GetProperty("id").GetGuid();
        var before = await api.Rows.EntriesAsync(account.Id);

        using var response = await PostWithReferenceAsync(account, route, entryId, externalReference);

        await response.ShouldBeValidationErrorAsync("externalReference");
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(before.Count);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task ExternalReferenceMustMatchFormatAndMatchingOnesAreStored(string route)
    {
        var account = await FundedAccountAsync();
        var entryId = (await api.Client.CreditOkAsync(account.Id, "1.00")).GetProperty("id").GetGuid();
        var before = await api.Rows.EntriesAsync(account.Id);

        using var response = await PostWithReferenceAsync(account, route, entryId, ReferenceOf64);
        var after = await api.Rows.EntriesAsync(account.Id);

        ReferenceOf64.Length.ShouldBe(64);
        (await response.ShouldBeEntryAsync()).GetProperty("externalReference").GetString().ShouldBe(ReferenceOf64);
        after.Count.ShouldBe(before.Count + 1);
        after[^1].ExternalReference.ShouldBe(ReferenceOf64);
    }

    private static string CategoryFor(string route) => route == "credits" ? "PIX_IN" : "PIX_OUT";

    // Conta com limite no teto: qualquer débito válido do teste é aceito.
    private async Task<TestAccount> FundedAccountAsync()
    {
        var account = await api.OpenAccountAsync();
        await api.Client.SetCreditLimitOkAsync(account.Id, "999999999999.99");
        await api.Client.CreditOkAsync(account.Id, "100.00");
        return account;
    }

    // Referência ausente quando `null`; no estorno ela é o único campo do corpo.
    private Task<HttpResponseMessage> PostWithReferenceAsync(TestAccount account, string route, Guid entryId, string? externalReference)
    {
        var reference = externalReference is null ? string.Empty : $$""", "externalReference": "{{externalReference}}" """;
        return route == "reversal"
            ? api.Client.SendRawAsync(HttpMethod.Post, $"/v1/accounts/{account.Id}/entries/{entryId}/reversal",
                externalReference is null ? "{}" : $$"""{ "externalReference": "{{externalReference}}" }""", LedgerTokens.Backoffice)
            : PostAsync(account, route, $$"""{ "amount": "1.00", "category": "{{CategoryFor(route)}}"{{reference}} }""");
    }

    private Task<HttpResponseMessage> PostAsync(TestAccount account, string route, string body) =>
        api.Client.SendRawAsync(
            HttpMethod.Post,
            $"/v1/accounts/{account.Id}/{route}",
            body,
            route == "credits" ? LedgerTokens.PaymentsHub : LedgerTokens.Channel(account.CustomerId));
}
