using IntegrationTests.Infrastructure;

namespace IntegrationTests.Customers;

// O seed de Development grava ana e bruno com os ids do realm. Num banco próprio: o CPF da ana é o da cliente dos
// exemplos, que os outros testes da coleção cadastram.
[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class CustomerSeedTests(CustomersEnvironment environment)
{
    private const string Ana = "11111111-1111-4111-8111-111111111111";
    private const string Bruno = "22222222-2222-4222-8222-222222222222";

    [Fact]
    public async Task SeedCreatesDevelopmentCustomers()
    {
        await using var database = await PostgresDatabase.StartAsync();
        var migration = await MigratorProcess.RunAsync(database.ConnectionStringFor("migrator"));
        migration.ExitCode.ShouldBe(0, migration.Output);
        var log = new LogSink();

        using var client = environment.CreateClient(log, hostEnvironment: "Development", settings: new Dictionary<string, string>
        {
            ["Customers:Seed:Enabled"] = "true",
            ["ConnectionStrings:Customers"] = database.ConnectionStringFor("customers_app", pooling: true),
            ["ConnectionStrings:Ledger"] = database.ConnectionStringFor("ledger_app", pooling: true),
        });

        await Eventually.WaitUntilAsync(
            async () => await database.ScalarAsync<long>(
                "postgres", $"SELECT count(*) FROM customers.customers WHERE id IN ('{Ana}', '{Bruno}')") == 2,
            TimeSpan.FromSeconds(60),
            () => $"the seed did not create ana and bruno\n{string.Join('\n', log.Since(0).Select(line => $"{line.Level} {line.Category}: {line.Message} {line.Exception}"))}",
            TimeSpan.FromMilliseconds(200));

        (await database.ScalarAsync<long>("postgres", "SELECT count(*) FROM customers.customers")).ShouldBe(2);
    }
}
