using IntegrationTests.Identity;
using IntegrationTests.Ledger;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class CustomersScopeTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task RegistrationRequiresCustomersWrite()
    {
        var before = await environment.Database.CustomerCountAsync();

        using var anonymous = await environment.Client.SendJsonAsync(
            HttpMethod.Post, "/v1/customers", new { name = "Ana", cpf = CustomersClient.NewCpf(), birthDate = "1990-04-15", email = "a@b.co" }, token: null);
        await anonymous.ShouldBeAuthenticationRequiredAsync();
        foreach (var scope in new[] { "customers.read", "customers.pii.read", "ledger.accounts.manage" })
        {
            using var forbidden = await environment.Client.RegisterAsync(CustomersClient.NewCustomer(), token: CustomersTokens.Only(scope));
            await forbidden.ShouldBeInsufficientScopeAsync();
        }

        (await environment.Database.CustomerCountAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task EachReadRouteRequiresItsScope()
    {
        var id = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());

        using var maskedAnonymous = await environment.Client.MaskedAsync(id, token: null);
        using var completeAnonymous = await environment.Client.PersonalDataAsync(id, token: null);
        using var completeWithRead = await environment.Client.PersonalDataAsync(id, CustomersTokens.Only("customers.read"));
        using var maskedWithPii = await environment.Client.MaskedAsync(id, CustomersTokens.Only("customers.pii.read"));
        using var maskedWithWrite = await environment.Client.MaskedAsync(id, CustomersTokens.Only("customers.write"));
        using var completeWithWrite = await environment.Client.PersonalDataAsync(id, CustomersTokens.Only("customers.write"));
        using var maskedWithRead = await environment.Client.MaskedAsync(id, CustomersTokens.Only("customers.read"));
        using var completeWithPii = await environment.Client.PersonalDataAsync(id, CustomersTokens.Only("customers.pii.read"));

        await maskedAnonymous.ShouldBeAuthenticationRequiredAsync();
        await completeAnonymous.ShouldBeAuthenticationRequiredAsync();
        await completeWithRead.ShouldBeInsufficientScopeAsync();
        await maskedWithPii.ShouldBeInsufficientScopeAsync();
        await maskedWithWrite.ShouldBeInsufficientScopeAsync();
        await completeWithWrite.ShouldBeInsufficientScopeAsync();
        // A recusa vem do escopo, não de outra falha: com o escopo certo, cada rota responde.
        await maskedWithRead.ShouldBeCustomerAsync();
        await completeWithPii.ShouldBeCustomerAsync();
    }
}
