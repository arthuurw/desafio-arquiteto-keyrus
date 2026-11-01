using System.Text;
using IntegrationTests.Infrastructure;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class BlindIndexTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task StoredIndexIsTransitHmacOfDigits()
    {
        var ana = await environment.AnaAsync();
        var row = (await environment.Database.CustomerAsync(ana.Id)).ShouldNotBeNull();
        var (expected, version) = await environment.Vault.HmacAsync("customers-blind-index", Encoding.UTF8.GetBytes("52998224725"));

        row.CpfBlindIndex.ShouldBe(expected);
        row.CpfBlindIndexVersion.ShouldBe(1);
        version.ShouldBe(1);

        // Rotação num cofre próprio: no da coleção, ela mudaria o índice de todo CPF dos outros testes.
        await using var rotated = await OpenBaoVault.StartAsync();
        await rotated.RotateAsync("customers-blind-index");
        using var client = environment.CreateClient(new LogSink(), rotated.Address);
        var id = await client.RegisteredAsync(CustomersClient.NewCustomer());

        (await environment.Database.CustomerAsync(id)).ShouldNotBeNull().CpfBlindIndexVersion.ShouldBe(2);
    }
}
