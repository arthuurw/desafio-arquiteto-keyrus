using ArchitectureTests.Rules;
using FixtureAlpha;

namespace ArchitectureTests;

public sealed class PersonalDataBoundaryTests
{
    [Fact]
    public void RuleFlagsFixtureWithCpfProperty()
    {
        PersonalDataRules.Violations(typeof(FixtureAlphaModule).Assembly)
            // A regra olha só o nome: o `Name` do ponto de composição da fixture também conta.
            .ShouldBe(["FixtureAlpha.Features.CustomerSnapshot.Cpf", "FixtureAlpha.FixtureAlphaModule.Name"]);
    }

    [Fact]
    public void PersonalDataStaysInsideCustomers()
    {
        PersonalDataRules.Violations(
                typeof(Ledger.LedgerModule).Assembly,
                typeof(Positions.PositionsModule).Assembly,
                typeof(Ledger.Contracts.ILedgerQueries).Assembly,
                // Ainda sem tipos: nenhum assembly o referencia, e ele é carregado pelo nome.
                System.Reflection.Assembly.Load("Positions.Contracts"),
                typeof(Customers.Contracts.ICustomerQueries).Assembly)
            .ShouldBeEmpty();
    }
}
