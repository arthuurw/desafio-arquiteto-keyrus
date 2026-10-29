using ArchitectureTests.Rules;

namespace ArchitectureTests;

public sealed class ModuleBoundaryTests
{
    [Fact]
    public void RuleRejectsFixtureUsingAnotherModuleInternals()
    {
        var violations = ModuleRules.BoundaryViolations([Modules.FixtureAlpha, Modules.FixtureBeta]);

        violations.ShouldContain(violation => violation.Contains("FixtureAlpha.UsesBetaInternals", StringComparison.Ordinal));
    }

    [Fact]
    public void ModulesDoNotUseOtherModulesInternals()
    {
        var violations = ModuleRules.BoundaryViolations(Modules.Real);

        violations.ShouldBeEmpty();
    }
}
