using Customers.Infrastructure.Vault;
using IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests.Customers;

// A Api principal do ambiente sobe com a folga de teste: o prazo de produção só vale onde o estouro é provocado.
[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class TestTimeoutTests(CustomersEnvironment environment)
{
    [Fact]
    public void EnvironmentsUseTheTestBudget() =>
        environment.ApiServices.GetRequiredService<OpenBaoSettings>().Timeout.ShouldBe(TimeSpan.Parse(TestTimeouts.Budget, null));
}
