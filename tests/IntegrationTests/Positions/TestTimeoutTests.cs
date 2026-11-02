using IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Positions.Features;

namespace IntegrationTests.Positions;

// A Api principal do ambiente sobe com a folga de teste: o prazo de produção só vale onde o estouro é provocado.
[Collection(PositionsApiDefinition.Name)]
public sealed class TestTimeoutTests(PositionsEnvironment environment)
{
    [Fact]
    public void EnvironmentsUseTheTestBudget() =>
        environment.ApiServices.GetRequiredService<ReadTimeout>().Value.ShouldBe(TimeSpan.Parse(TestTimeouts.Budget, null));
}
