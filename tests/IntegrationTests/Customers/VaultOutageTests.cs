using System.Diagnostics;
using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;
using IntegrationTests.Positions;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class VaultOutageTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task LedgerAndPositionsIgnoreTheVault()
    {
        var customer = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());

        await using (await environment.Vault.StopAsync())
        {
            var account = await environment.OpenAccountAsync(customer);
            using var credit = await environment.Client.CreditAsync(account.Id, "100.00");
            var debit = await environment.Client.DebitOkAsync(account, "30.00");
            using var reversal = await environment.Client.ReverseAsync(account.Id, debit.GetProperty("id").GetGuid());
            var balance = await environment.Client.BalanceAsync(account.Id);
            var statement = await environment.Client.StatementAsync(account.Id);
            using var customerRead = await environment.Client.MaskedAsync(customer, CustomersTokens.Backoffice);

            account.CustomerId.ShouldBe(customer);
            await credit.ShouldBeEntryAsync();
            await reversal.ShouldBeEntryAsync();
            balance.Status.ShouldBe(200, balance.Text);
            statement.Status.ShouldBe(200, statement.Text);
            await customerRead.ShouldBeProblemAsync(503, "personal-data-unavailable");
        }
    }

    [Fact]
    public async Task RegistrationFailsWithoutConsumingTheKey()
    {
        var customer = CustomersClient.NewCustomer();
        var key = Guid.NewGuid().ToString();
        var customersBefore = await environment.Database.CustomerCountAsync();
        var keysBefore = await environment.Database.KeyCountAsync();

        await using (await environment.Vault.StopAsync())
        {
            using var refused = await environment.Client.RegisterAsync(customer, key);

            await refused.ShouldBeProblemAsync(503, "personal-data-unavailable");
            refused.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(5));
            (await environment.Database.CustomerCountAsync()).ShouldBe(customersBefore);
            (await environment.Database.KeyCountAsync()).ShouldBe(keysBefore);
        }

        using var accepted = await environment.Client.RegisterAsync(customer, key);

        await accepted.ShouldBeCreatedAsync();
        accepted.IsReplay().ShouldBeFalse();
    }

    [Fact]
    public async Task RegistrationWithTheVaultDownIsAudited()
    {
        var customer = CustomersClient.NewCustomer();

        TracedResponse refused;
        await using (await environment.Vault.StopAsync())
        {
            refused = await environment.Client.SendTracedAsync(
                environment.Completions,
                HttpMethod.Post,
                "/v1/customers",
                customer.RegistrationBody,
                CustomersTokens.Backoffice,
                Guid.NewGuid().ToString());
        }

        (refused.Status, refused.ProblemType).ShouldBe((503, "personal-data-unavailable"));
        var row = await environment.Database.SingleAuditRowAsync("customers", refused);
        (row.ClientId, row.Operation, row.ResourceType, row.ResourceId, row.Result).ShouldBe(("backoffice", "register-customer", "customer", (Guid?)null, 503));
    }

    [Fact]
    public async Task PersonalDataReadWithTheVaultDownIsAudited()
    {
        // Nunca lido: a DEK não está no cache, e a leitura precisa do cofre.
        var id = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());

        TracedResponse refused;
        await using (await environment.Vault.StopAsync())
        {
            refused = await environment.Client.SendTracedAsync(
                environment.Completions, HttpMethod.Get, $"/v1/customers/{id}/personal-data", body: null, CustomersTokens.Compliance);
        }

        (refused.Status, refused.ProblemType).ShouldBe((503, "personal-data-unavailable"));
        var row = await environment.Database.SingleAuditRowAsync("customers", refused);
        (row.ClientId, row.Operation, row.ResourceType, row.ResourceId, row.Result).ShouldBe(("compliance", "read-personal-data", "customer", (Guid?)id, 503));
    }

    [Fact]
    public async Task SlowVaultTimesOutAfterOneSecond()
    {
        var customer = CustomersClient.NewCustomer();
        var key = Guid.NewGuid().ToString();
        var customersBefore = await environment.Database.CustomerCountAsync();
        var keysBefore = await environment.Database.KeyCountAsync();
        await using var silent = new SilentVault();
        using var client = environment.CreateClient(new LogSink(), silent.Address);

        var elapsed = Stopwatch.StartNew();
        using var response = await client.RegisterAsync(customer, key);
        elapsed.Stop();

        await response.ShouldBeProblemAsync(503, "personal-data-unavailable");
        response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(5));
        elapsed.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        (await environment.Database.CustomerCountAsync()).ShouldBe(customersBefore);
        (await environment.Database.KeyCountAsync()).ShouldBe(keysBefore);

        using var accepted = await environment.Client.RegisterAsync(customer, key);

        await accepted.ShouldBeCreatedAsync();
        accepted.IsReplay().ShouldBeFalse();
    }

    [Fact]
    public async Task SealedVaultAnswersUnavailableWithoutConsumingTheKey()
    {
        var registered = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var customer = CustomersClient.NewCustomer();
        var key = Guid.NewGuid().ToString();
        var customersBefore = await environment.Database.CustomerCountAsync();
        var keysBefore = await environment.Database.KeyCountAsync();
        await using var sealedVault = new SealedVault();
        await sealedVault.StartAsync();
        using var client = environment.CreateClient(new LogSink(), sealedVault.Address);

        using var refused = await client.RegisterAsync(customer, key);
        using var read = await client.MaskedAsync(registered, CustomersTokens.Backoffice);

        await refused.ShouldBeProblemAsync(503, "personal-data-unavailable");
        refused.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(5));
        await read.ShouldBeProblemAsync(503, "personal-data-unavailable");
        read.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(5));
        sealedVault.Requests.ShouldBeGreaterThan(0);
        (await environment.Database.CustomerCountAsync()).ShouldBe(customersBefore);
        (await environment.Database.KeyCountAsync()).ShouldBe(keysBefore);

        using var accepted = await environment.Client.RegisterAsync(customer, key);

        await accepted.ShouldBeCreatedAsync();
        accepted.IsReplay().ShouldBeFalse();
    }
}
