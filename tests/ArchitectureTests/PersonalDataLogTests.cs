using ArchitectureTests.Rules;
using FixtureGamma.Contracts;

namespace ArchitectureTests;

// Dado pessoal só vai para um método de log marcado `[PersonalData]`, que a redação apaga (ADR-0041).
public sealed class PersonalDataLogTests
{
    [Fact]
    public void RuleFlagsUnmarkedPersonalLogParameters()
    {
        PersonalDataRules.UnmarkedLogParameters(typeof(ContactSnapshot).Assembly)
            .ShouldBe(["FixtureGamma.Logging.UnmarkedCustomerLogs.LogNamed(name)", "FixtureGamma.Logging.UnmarkedCustomerLogs.LogReached(email)"]);
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
