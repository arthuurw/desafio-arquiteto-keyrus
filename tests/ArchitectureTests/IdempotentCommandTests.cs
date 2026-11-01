using System.Reflection;
using ArchitectureTests.Rules;
using FixtureAlpha;
using Ledger;

namespace ArchitectureTests;

public sealed class IdempotentCommandTests
{
    [Fact]
    public void RuleFlagsFixtureCommandWithoutInterface()
    {
        var fixture = typeof(FixtureAlphaModule).Assembly;

        var violations = IdempotentCommandRules.Violations(
            fixture, TypeNamed(fixture, "FixtureAlpha.Features.IFixtureIdempotentCommand"), TypeNamed(fixture, "FixtureAlpha.Features.ExemptCommand"));

        violations.ShouldBe(["FixtureAlpha.Features.UnprotectedCommand"]);
    }

    [Fact]
    public void EveryLedgerCommandIsIdempotentExceptCreditLimit()
    {
        var ledger = typeof(LedgerModule).Assembly;
        var idempotent = TypeNamed(ledger, "Ledger.Infrastructure.Idempotency.IIdempotentCommand");

        var violations = IdempotentCommandRules.Violations(
            ledger, idempotent, TypeNamed(ledger, "Ledger.Features.ChangeCreditLimit.ChangeCreditLimitCommand"));

        violations.ShouldBeEmpty();
        // A regra não passa só por não achar comandos: os três de escrita com chave são encontrados.
        IdempotentCommandRules.Protected(ledger, idempotent).ShouldBe([
            "Ledger.Features.OpenAccount.OpenAccountCommand",
            "Ledger.Features.RecordEntry.RecordEntryCommand",
            "Ledger.Features.ReverseEntry.ReverseEntryCommand",
        ]);
    }

    [Fact]
    public void EveryCustomersCommandIsIdempotent()
    {
        var customers = typeof(Customers.CustomersModule).Assembly;
        var idempotent = TypeNamed(customers, "Customers.Infrastructure.Idempotency.IIdempotentCommand");

        IdempotentCommandRules.Violations(customers, idempotent).ShouldBeEmpty();
        // A regra não passa só por não achar comandos: o cadastro é encontrado.
        IdempotentCommandRules.Protected(customers, idempotent).ShouldBe(["Customers.Features.RegisterCustomer.RegisterCustomerCommand"]);
    }

    private static Type TypeNamed(Assembly assembly, string fullName) =>
        assembly.GetType(fullName, throwOnError: true)!;
}
