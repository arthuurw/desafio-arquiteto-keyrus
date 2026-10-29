using System.Reflection;

namespace ArchitectureTests.Rules;

// Um módulo avaliado pelas regras: nome (raiz dos namespaces) e projeto interno.
public sealed record ModuleUnderTest(string Name, Assembly Internal)
{
    internal string EntryTypeFullName => $"{Name}.{Name}Module";

    public override string ToString() => Name;
}

internal static class Modules
{
    internal static IReadOnlyList<ModuleUnderTest> Real { get; } =
    [
        new("Ledger", typeof(Ledger.LedgerModule).Assembly),
        new("Positions", typeof(Positions.PositionsModule).Assembly),
        new("Customers", typeof(Customers.CustomersModule).Assembly),
    ];

    internal static ModuleUnderTest FixtureAlpha { get; } = new("FixtureAlpha", typeof(FixtureAlpha.FixtureAlphaModule).Assembly);

    internal static ModuleUnderTest FixtureBeta { get; } = new("FixtureBeta", typeof(FixtureBeta.FixtureBetaModule).Assembly);
}
