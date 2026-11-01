using System.Globalization;
using System.Text.Json;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class CustomerReadTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task MaskedReadShowsMaskedFields()
    {
        var ana = await environment.AnaAsync();
        var row = (await environment.Database.CustomerAsync(ana.Id)).ShouldNotBeNull();

        using var response = await environment.Client.MaskedAsync(ana.Id, CustomersTokens.Backoffice);

        var body = await response.ShouldBeCustomerAsync();
        Fields(body).ShouldBe(new Dictionary<string, string>
        {
            ["id"] = ana.Id.ToString("D"),
            ["name"] = "Ana M. S.",
            ["cpf"] = "***.982.247-**",
            ["birthDate"] = "****-**-15",
            ["email"] = "a***@ledger.local",
            ["registeredAt"] = Iso(row.RegisteredAt),
        });
    }

    [Fact]
    public async Task PersonalDataReturnsStoredValues()
    {
        var ana = await environment.AnaAsync();
        var row = (await environment.Database.CustomerAsync(ana.Id)).ShouldNotBeNull();
        var bia = await environment.Client.RegisteredAsync(new CustomerInput("  Bia  ", "11144477735", "2001-12-09", "bia@ledger.local"));

        using var anaResponse = await environment.Client.PersonalDataAsync(ana.Id, CustomersTokens.Compliance);
        using var biaResponse = await environment.Client.PersonalDataAsync(bia, CustomersTokens.Compliance);

        Fields(await anaResponse.ShouldBeCustomerAsync()).ShouldBe(new Dictionary<string, string>
        {
            ["id"] = ana.Id.ToString("D"),
            ["name"] = "Ana Maria Souza",
            ["cpf"] = "529.982.247-25",
            ["birthDate"] = "1990-04-15",
            ["email"] = "ana.souza@ledger.local",
            ["registeredAt"] = Iso(row.RegisteredAt),
        });
        var biaBody = await biaResponse.ShouldBeCustomerAsync();
        biaBody.GetProperty("cpf").GetString().ShouldBe("111.444.777-35");
        biaBody.GetProperty("name").GetString().ShouldBe("Bia");
    }

    // O banco guarda microssegundos; a resposta é o mesmo instante em UTC, terminado em `Z`.
    private static string Iso(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Fields(JsonElement body) =>
        body.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!);
}
