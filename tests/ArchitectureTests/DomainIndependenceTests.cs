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

    [Theory]
    [InlineData("FixtureAlpha.Domain.DependsOnNpgsqlTypes")]
    [InlineData("FixtureAlpha.Domain.DependsOnHttpHeaders")]
    public void RuleRejectsFixtureDomainDependingOnForbiddenAssemblies(string offendingType)
    {
        var violations = ModuleRules.DomainViolations([Modules.FixtureAlpha]);

        violations.ShouldContain(violation => violation.Contains(offendingType, StringComparison.Ordinal));
    }

    [Fact]
    public void RuleRequiresDomainTypes()
    {
        var evaluated = ModuleRules.DomainTypes(Modules.Ledger);
        var missingDomain = ModuleRules.DomainViolations([Modules.Ledger with { Name = "NoSuchModule" }]);

        evaluated.Count.ShouldBeGreaterThanOrEqualTo(5);
        evaluated.ShouldContain("Account");
        evaluated.ShouldContain("Entry");
        evaluated.ShouldContain("Money");
        evaluated.ShouldContain(name => name.StartsWith("Outcome", StringComparison.Ordinal));
        evaluated.ShouldContain("LedgerError");
        missingDomain.ShouldNotBeEmpty();
    }

    [Fact]
    public void ModuleDomainsDoNotDependOnInfrastructure()
    {
        var violations = ModuleRules.DomainViolations(Modules.Real);

        violations.ShouldBeEmpty();
    }
}
