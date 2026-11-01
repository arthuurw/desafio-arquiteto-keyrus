using ArchitectureTests.Rules;
using FixtureAlpha;

namespace ArchitectureTests;

public sealed class CommandDispatchTests
{
    [Fact]
    public void RuleFlagsFixtureEndpointThatDependsOnIMediator()
    {
        var violations = CommandDispatchRules.Violations(typeof(FixtureAlphaModule).Assembly, "FixtureAlpha");

        violations.ShouldBe(["FixtureAlpha.Features.MediatorEndpoint"]);
    }

    [Fact]
    public void OnlyTheFacadesDispatchCommands()
    {
        var ledger = typeof(Ledger.LedgerModule).Assembly;

        CommandDispatchRules.Violations(ledger, "Ledger", "Ledger.Features.LedgerCommands").ShouldBeEmpty();

        // A regra não passa por não enxergar o mediador: a facade depende do gerado pelo próprio módulo.
        CommandDispatchRules.DependsOnDispatcher(ledger.GetType("Ledger.Features.LedgerCommands", throwOnError: true)!, "Ledger")
            .ShouldBeTrue();
    }
}
