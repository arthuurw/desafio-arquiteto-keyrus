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
            fixture, Internal(fixture, "FixtureAlpha.Features.IFixtureIdempotentCommand"), Internal(fixture, "FixtureAlpha.Features.ExemptCommand"));

        violations.ShouldBe(["FixtureAlpha.Features.UnprotectedCommand"]);
    }

    [Fact]
    public void EveryLedgerCommandIsIdempotentExceptCreditLimit()
    {
        var ledger = typeof(LedgerModule).Assembly;
        var idempotent = Internal(ledger, "Ledger.Features.Idempotency.IIdempotentCommand");

        var violations = IdempotentCommandRules.Violations(
            ledger, idempotent, Internal(ledger, "Ledger.Features.ChangeCreditLimit.ChangeCreditLimitCommand"));

        violations.ShouldBeEmpty();
        // A regra não passa só por não achar comandos: os três de escrita com chave são encontrados.
        IdempotentCommandRules.Protected(ledger, idempotent).ShouldBe([
            "Ledger.Features.OpenAccount.OpenAccountCommand",
            "Ledger.Features.RecordEntry.RecordEntryCommand",
            "Ledger.Features.ReverseEntry.ReverseEntryCommand",
        ]);
    }

    private static Type Internal(Assembly assembly, string fullName) =>
        assembly.GetType(fullName, throwOnError: true)!;
}
