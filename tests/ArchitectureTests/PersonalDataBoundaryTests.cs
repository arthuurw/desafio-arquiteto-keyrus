using ArchitectureTests.Rules;
using FixtureAlpha;
using FixtureGamma.Contracts;

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
    public void RuleFlagsMemberClassifiedOutsideCustomers()
    {
        PersonalDataRules.MarkedMembers(typeof(ContactSnapshot).Assembly)
            .ShouldBe(["FixtureGamma.Contracts.ContactSnapshot.Phone"]);
    }

    // Dado classificado como pessoal é só de Customers: nenhum tipo de Ledger, Positions ou dos contratos o carrega
    // (ADR-0017).
    [Fact]
    public void NoClassifiedMemberOutsideCustomers()
    {
        PersonalDataRules.MarkedMembers(
                typeof(Ledger.LedgerModule).Assembly,
                typeof(Positions.PositionsModule).Assembly,
                typeof(Ledger.Contracts.ILedgerQueries).Assembly,
                System.Reflection.Assembly.Load("Positions.Contracts"),
                typeof(Customers.Contracts.ICustomerQueries).Assembly)
            .ShouldBeEmpty();
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
