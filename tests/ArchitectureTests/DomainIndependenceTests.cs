using ArchitectureTests.Rules;

namespace ArchitectureTests;

public sealed class DomainIndependenceTests
{
    [Theory]
    [InlineData("FixtureAlpha.Domain.DependsOnEfCore")]
    [InlineData("FixtureAlpha.Domain.DependsOnNpgsql")]
    [InlineData("FixtureAlpha.Domain.DependsOnMongoDb")]
    [InlineData("FixtureAlpha.Domain.DependsOnAspNetCore")]
    [InlineData("FixtureAlpha.Domain.DependsOnInfrastructure")]
    public void RuleRejectsFixtureDomainDependingOnInfrastructure(string offendingType)
    {
        var violations = ModuleRules.DomainViolations([Modules.FixtureAlpha]);

        violations.ShouldContain(violation => violation.Contains(offendingType, StringComparison.Ordinal));
    }

    [Fact]
    public void ModuleDomainsDoNotDependOnInfrastructure()
    {
        var violations = ModuleRules.DomainViolations(Modules.Real);

        violations.ShouldBeEmpty();
    }
}
