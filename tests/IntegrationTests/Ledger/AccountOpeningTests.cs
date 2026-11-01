using IntegrationTests.Infrastructure;
using IntegrationTests.Identity;
using static IntegrationTests.Identity.TestTokenIssuer;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class AccountOpeningTests(LedgerApi api)
{
    [Fact]
    public async Task OpensAccountWithZeroBalanceAndLimit()
    {
        var customerId = Guid.NewGuid();
        await api.Database.SeedCustomerAsync(customerId);

        using var first = await api.Client.SendJsonAsync(HttpMethod.Post, "/v1/accounts", new { customerId }, LedgerTokens.Backoffice);
        using var second = await api.Client.SendJsonAsync(HttpMethod.Post, "/v1/accounts", new { customerId }, LedgerTokens.Backoffice);
        var firstId = (await first.JsonAsync()).GetProperty("id").GetGuid();
        var secondId = (await second.JsonAsync()).GetProperty("id").GetGuid();
        var row = await api.Rows.AccountAsync(firstId);

        ((int)first.StatusCode).ShouldBe(201);
        ((int)second.StatusCode).ShouldBe(201);
        firstId.Version.ShouldBe(7);
        secondId.Version.ShouldBe(7);
        secondId.ShouldNotBe(firstId);
        row.ShouldNotBeNull();
        row.CustomerId.ShouldBe(customerId);
        row.Balance.ShouldBe(0m);
        row.CreditLimit.ShouldBe(0m);
        row.Version.ShouldBe(0);
        (await api.Rows.AccountAsync(secondId)).ShouldNotBeNull().CustomerId.ShouldBe(customerId);
    }

    // A recusa fica com a chave: após o cadastro, a repetição ainda recusa e só uma chave nova abre a conta (ADR-0037).
    [Fact]
    public async Task UnregisteredCustomerGetsNoAccount()
    {
        var customerId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var before = await AccountsOfAsync(customerId);

        using var unregistered = await api.Client.SendJsonAsync(HttpMethod.Post, "/v1/accounts", new { customerId }, LedgerTokens.Backoffice, key);
        var afterRefusal = await AccountsOfAsync(customerId);
        await api.Database.SeedCustomerAsync(customerId);
        using var repeated = await api.Client.SendJsonAsync(HttpMethod.Post, "/v1/accounts", new { customerId }, LedgerTokens.Backoffice, key);
        var afterRepeat = await AccountsOfAsync(customerId);
        using var withNewKey = await api.Client.SendJsonAsync(HttpMethod.Post, "/v1/accounts", new { customerId }, LedgerTokens.Backoffice);

        before.ShouldBe(0);
        await unregistered.ShouldBeProblemAsync(404, "customer-not-found");
        unregistered.IsReplay().ShouldBeFalse();
        afterRefusal.ShouldBe(0);
        await repeated.ShouldBeProblemAsync(404, "customer-not-found");
        repeated.IsReplay().ShouldBeTrue();
        afterRepeat.ShouldBe(0);
        var created = await withNewKey.ShouldBeCreatedAsync();
        (await api.Rows.AccountAsync(created["id"]!.GetValue<Guid>())).ShouldNotBeNull().CustomerId.ShouldBe(customerId);
        (await AccountsOfAsync(customerId)).ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{ "customerId": "abc" }""")]
    [InlineData("""{ "customerId": "00000000-0000-0000-0000-000000000000" }""")]
    public async Task RejectsInvalidCustomerId(string body)
    {
        var before = await api.Rows.AccountCountAsync();

        using var response = await api.Client.SendRawAsync(HttpMethod.Post, "/v1/accounts", body, LedgerTokens.Backoffice);

        await response.ShouldBeValidationErrorAsync("customerId");
        (await api.Rows.AccountCountAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task DelegatedCallerOpensOnlyOwnAccount()
    {
        var customerA = Guid.NewGuid();
        var customerB = Guid.NewGuid();
        await api.Database.SeedCustomerAsync(customerA);
        await api.Database.SeedCustomerAsync(customerB);
        var delegatedA = Sign(Delegated(customerA.ToString(), "ledger.accounts.manage"));
        var key = Guid.NewGuid().ToString();

        using var forOther = await api.Client.SendJsonAsync(HttpMethod.Post, "/v1/accounts", new { customerId = customerB }, delegatedA, key);
        using var repeated = await api.Client.SendJsonAsync(HttpMethod.Post, "/v1/accounts", new { customerId = customerB }, delegatedA, key);
        var accountsOfB = await AccountsOfAsync(customerB);
        using var forSelf = await api.Client.SendJsonAsync(HttpMethod.Post, "/v1/accounts", new { customerId = customerA }, delegatedA);
        using var byService = await api.Client.SendJsonAsync(HttpMethod.Post, "/v1/accounts", new { customerId = customerB }, LedgerTokens.Backoffice);

        await forOther.ShouldBeProblemAsync(404, "customer-not-found");
        forOther.IsReplay().ShouldBeFalse();
        await repeated.ShouldBeProblemAsync(404, "customer-not-found");
        repeated.IsReplay().ShouldBeTrue();
        accountsOfB.ShouldBe(0);
        await forSelf.ShouldBeCreatedAsync();
        await byService.ShouldBeCreatedAsync();
        (await AccountsOfAsync(customerA)).ShouldBe(1);
        (await AccountsOfAsync(customerB)).ShouldBe(1);
    }

    [Fact]
    public async Task CreatedAtComesFromDatabaseClock()
    {
        var columnDefault = await api.Database.ScalarAsync<string>("postgres", """
            SELECT column_default FROM information_schema.columns
            WHERE table_schema = 'ledger' AND table_name = 'accounts' AND column_name = 'created_at'
            """);
        var before = await DatabaseClockAsync();
        var account = await api.OpenAccountAsync();
        var after = await DatabaseClockAsync();
        await api.Client.CreditOkAsync(account.Id, "1.00");

        var createdAt = (await api.Database.ScalarAsync<DateTime>(
            "postgres", $"SELECT created_at FROM ledger.accounts WHERE id = '{account.Id}'"));
        var firstRecordedAt = (await api.Rows.EntriesAsync(account.Id)).ShouldHaveSingleItem().RecordedAt;

        columnDefault.ShouldBe("clock_timestamp()");
        createdAt.ShouldBeGreaterThanOrEqualTo(before);
        createdAt.ShouldBeLessThanOrEqualTo(after);
        createdAt.ShouldBeLessThanOrEqualTo(firstRecordedAt);
    }

    private Task<DateTime> DatabaseClockAsync() => api.Database.ScalarAsync<DateTime>("postgres", "SELECT clock_timestamp()");

    private Task<long> AccountsOfAsync(Guid customerId) =>
        api.Database.ScalarAsync<long>("postgres", $"SELECT count(*) FROM ledger.accounts WHERE customer_id = '{customerId}'");
}
