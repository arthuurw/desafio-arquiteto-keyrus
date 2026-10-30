namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class EntryValidationTests(LedgerApi api)
{
    // `amount` em JSON cru, para `10.5` chegar como número JSON e provar que só string é aceita.
    public static TheoryData<string, string, int, string?> Amounts
    {
        get
        {
            var data = new TheoryData<string, string, int, string?>();
            foreach (var route in new[] { "credits", "debits" })
            {
                foreach (var invalid in new[] { "\"0.00\"", "\"-1.00\"", "\"10.001\"", "\"10.100\"", "10.5", "\"abc\"", "\"1000000000000.00\"" })
                {
                    data.Add(route, invalid, 400, null);
                }

                data.Add(route, "\"999999999999.99\"", 201, "999999999999.99");
                data.Add(route, "\"10.1\"", 201, "10.10");
            }

            return data;
        }
    }

    public static TheoryData<string, string, int> Categories
    {
        get
        {
            var data = new TheoryData<string, string, int>();
            foreach (var category in new[] { "PIX_IN", "TED_IN", "BOLETO_IN", "DEPOSIT", "ADJUSTMENT_IN" })
            {
                data.Add("credits", category, 201);
                data.Add("debits", category, 400);
            }

            foreach (var category in new[] { "PIX_OUT", "TED_OUT", "BOLETO_OUT", "CARD", "FEE", "ADJUSTMENT_OUT" })
            {
                data.Add("debits", category, 201);
                data.Add("credits", category, 400);
            }

            foreach (var category in new[] { "REVERSAL", "pix_in" })
            {
                data.Add("credits", category, 400);
                data.Add("debits", category, 400);
            }

            return data;
        }
    }

    public static TheoryData<string, string?, int> References
    {
        get
        {
            var data = new TheoryData<string, string?, int>();
            foreach (var route in new[] { "credits", "debits", "reversal" })
            {
                data.Add(route, null, 400);
                data.Add(route, "", 400);
                data.Add(route, new string('A', 65), 400);
                data.Add(route, "joao silva", 400);
                data.Add(route, "Ab0._:-" + new string('x', 57), 201);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Amounts))]
    public async Task AmountMustBePositiveWithTwoDecimals(string route, string amount, int status, string? storedAmount)
    {
        var account = await FundedAccountAsync();
        var before = await api.Rows.EntriesAsync(account.Id);

        using var response = await PostAsync(account, route, $$"""{ "amount": {{amount}}, "category": "{{CategoryFor(route)}}", "externalReference": "R-1" }""");
        var after = await api.Rows.EntriesAsync(account.Id);

        if (status == 201)
        {
            (await response.ShouldBeEntryAsync()).GetProperty("amount").GetString().ShouldBe(storedAmount);
            after.Count.ShouldBe(before.Count + 1);
        }
        else
        {
            await response.ShouldBeValidationErrorAsync("amount");
            after.Count.ShouldBe(before.Count);
        }
    }

    [Theory]
    [MemberData(nameof(Categories))]
    public async Task CategoryMustMatchEntryType(string route, string category, int status)
    {
        var account = await FundedAccountAsync();
        var before = await api.Rows.EntriesAsync(account.Id);

        using var response = await PostAsync(account, route, $$"""{ "amount": "1.00", "category": "{{category}}", "externalReference": "R-1" }""");
        var after = await api.Rows.EntriesAsync(account.Id);

        if (status == 201)
        {
            (await response.ShouldBeEntryAsync()).GetProperty("category").GetString().ShouldBe(category);
            after.Count.ShouldBe(before.Count + 1);
        }
        else
        {
            await response.ShouldBeValidationErrorAsync("category");
            after.Count.ShouldBe(before.Count);
        }
    }

    [Theory]
    [MemberData(nameof(References))]
    public async Task ExternalReferenceMustMatchFormat(string route, string? externalReference, int status)
    {
        var account = await FundedAccountAsync();
        var entryId = (await api.Client.CreditOkAsync(account.Id, "1.00")).GetProperty("id").GetGuid();
        var before = await api.Rows.EntriesAsync(account.Id);
        var reference = externalReference is null ? string.Empty : $$""", "externalReference": "{{externalReference}}" """;

        using var response = route == "reversal"
            ? await api.Client.SendRawAsync(HttpMethod.Post, $"/v1/accounts/{account.Id}/entries/{entryId}/reversal",
                externalReference is null ? "{}" : $$"""{ "externalReference": "{{externalReference}}" }""", LedgerTokens.Backoffice)
            : await PostAsync(account, route, $$"""{ "amount": "1.00", "category": "{{CategoryFor(route)}}"{{reference}} }""");
        var after = await api.Rows.EntriesAsync(account.Id);

        if (status == 201)
        {
            externalReference.ShouldNotBeNull().Length.ShouldBe(64);
            (await response.ShouldBeEntryAsync()).GetProperty("externalReference").GetString().ShouldBe(externalReference);
            after.Count.ShouldBe(before.Count + 1);
            after[^1].ExternalReference.ShouldBe(externalReference);
        }
        else
        {
            await response.ShouldBeValidationErrorAsync("externalReference");
            after.Count.ShouldBe(before.Count);
        }
    }

    private static string CategoryFor(string route) => route == "credits" ? "PIX_IN" : "PIX_OUT";

    // Conta com limite no teto: qualquer débito válido do teste é aceito.
    private async Task<TestAccount> FundedAccountAsync()
    {
        var account = await api.Client.OpenAccountAsync();
        await api.Client.SetCreditLimitOkAsync(account.Id, "999999999999.99");
        await api.Client.CreditOkAsync(account.Id, "100.00");
        return account;
    }

    private Task<HttpResponseMessage> PostAsync(TestAccount account, string route, string body) =>
        api.Client.SendRawAsync(
            HttpMethod.Post,
            $"/v1/accounts/{account.Id}/{route}",
            body,
            route == "credits" ? LedgerTokens.PaymentsHub : LedgerTokens.Channel(account.CustomerId));
}
