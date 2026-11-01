using IntegrationTests.Infrastructure;

namespace IntegrationTests.Ledger;

// Api e banco de um ambiente de teste: abrir conta exige o cliente cadastrado, semeado no banco antes.
internal interface ILedgerEnvironment
{
    HttpClient Client { get; }

    PostgresDatabase Database { get; }
}

internal static class LedgerEnvironment
{
    internal static async Task<TestAccount> OpenAccountAsync(this ILedgerEnvironment environment, Guid? customerId = null)
    {
        var customer = customerId ?? Guid.NewGuid();
        await environment.Database.SeedCustomerAsync(customer);
        return await environment.Client.OpenAccountAsync(customer);
    }
}
