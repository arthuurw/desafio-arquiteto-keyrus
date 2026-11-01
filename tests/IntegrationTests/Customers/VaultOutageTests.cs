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
    public async Task SlowVaultTimesOutAfterOneSecond()
    {
        await using var silent = new SilentVault();
        using var client = environment.CreateClient(new LogSink(), silent.Address);

        var elapsed = Stopwatch.StartNew();
        using var response = await client.RegisterAsync(CustomersClient.NewCustomer());
        elapsed.Stop();

        await response.ShouldBeProblemAsync(503, "personal-data-unavailable");
        elapsed.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }
}
