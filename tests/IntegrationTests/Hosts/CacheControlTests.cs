using IntegrationTests.Customers;
using IntegrationTests.Ledger;

namespace IntegrationTests.Hosts;

// Dado pessoal e financeiro não fica em cache de navegador nem de proxy: toda resposta `/v1` leva `no-store`, também
// as recusas.
[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class CacheControlTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task V1ResponsesAreNotStored()
    {
        var ana = await environment.AnaAsync();
        var account = await environment.OpenAccountAsync();

        using var personalData = await environment.Client.PersonalDataAsync(ana.Id, CustomersTokens.Compliance);
        using var credit = await environment.Client.CreditAsync(account.Id, "1.00");
        using var notFound = await environment.Client.CreditAsync(Guid.NewGuid(), "1.00");
        using var unauthenticated = await environment.Client.PersonalDataAsync(ana.Id, token: null);

        foreach (var (response, status) in new[] { (personalData, 200), (credit, 201), (notFound, 404), (unauthenticated, 401) })
        {
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            ((int)response.StatusCode).ShouldBe(status, text);
            response.Headers.CacheControl.ShouldNotBeNull($"{status} has no Cache-Control").NoStore.ShouldBeTrue($"{status}: {response.Headers.CacheControl}");
        }
    }
}
