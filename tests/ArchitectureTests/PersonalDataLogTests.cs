using ArchitectureTests.Rules;
using FixtureGamma.Contracts;

namespace ArchitectureTests;

// Dado pessoal só vai para o log em parâmetro marcado `[PersonalData]`, que a redação apaga (ADR-0041).
public sealed class PersonalDataLogTests
{
    [Fact]
    public void RuleFlagsUnmarkedPersonalLogParameters()
    {
        PersonalDataRules.UnmarkedLogParameters(typeof(ContactSnapshot).Assembly)
            .ShouldBe(
            [
                "FixtureGamma.Logging.UnmarkedCustomerLogs.LogNamed(name)",
                "FixtureGamma.Logging.UnmarkedCustomerLogs.LogReached(email)",
                "FixtureGamma.Logging.UnmarkedCustomerLogs.LogRead(snapshot)",
            ],
            ignoreOrder: true);
    }

    // O parâmetro `snapshot` não tem nome nem tipo de dado pessoal: a regra o pega pelos membros do tipo.
    [Fact]
    public void RuleFlagsTypeCarryingPersonalData()
    {
        var violations = PersonalDataRules.UnmarkedLogParameters(typeof(ContactSnapshot).Assembly);

        violations.ShouldContain("FixtureGamma.Logging.UnmarkedCustomerLogs.LogRead(snapshot)");
        violations.ShouldNotContain("FixtureGamma.Logging.MarkedCustomerLogs.LogRead(snapshot)");
    }

    [Fact]
    public void ProductionLogsMarkPersonalData()
    {
        PersonalDataRules.UnmarkedLogParameters(
                System.Reflection.Assembly.Load("Api"),
                System.Reflection.Assembly.Load("Worker"),
                System.Reflection.Assembly.Load("Migrator"),
                typeof(BuildingBlocks.Logging.PersonalDataAttribute).Assembly,
                typeof(Ledger.LedgerModule).Assembly,
                typeof(Positions.PositionsModule).Assembly,
                typeof(Customers.CustomersModule).Assembly,
                typeof(Ledger.Contracts.ILedgerQueries).Assembly,
                typeof(Customers.Contracts.ICustomerQueries).Assembly)
            .ShouldBeEmpty();
    }
}
