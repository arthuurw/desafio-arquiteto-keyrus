using ArchitectureTests.Rules;

namespace ArchitectureTests;

public sealed class ModuleVisibilityTests
{
    [Fact]
    public void RuleRejectsFixtureWithExtraPublicType()
    {
        var violations = ModuleRules.VisibilityViolations([Modules.FixtureAlpha]);

        violations.ShouldContain(violation => violation.Contains("FixtureAlpha.ExtraPublicType", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyModuleEntryTypeIsPublic()
    {
        var violations = ModuleRules.VisibilityViolations(Modules.Real);
        var publicTypes = Modules.Real.ToDictionary(
            module => module.Name,
            module => module.Internal.GetExportedTypes().Select(type => type.FullName).ToArray());

        violations.ShouldBeEmpty();
        publicTypes["Ledger"].ShouldBe(["Ledger.LedgerModule"]);
        publicTypes["Positions"].ShouldBe(["Positions.PositionsModule"]);
        publicTypes["Customers"].ShouldBe(["Customers.CustomersModule"]);
    }
}
