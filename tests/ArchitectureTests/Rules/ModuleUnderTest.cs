using System.Reflection;

namespace ArchitectureTests.Rules;

// Um módulo avaliado pelas regras: nome (raiz dos namespaces), projeto interno e se ele já tem domínio. Com domínio,
// a regra de independência sem tipo nenhum para avaliar reprova, em vez de passar sem ter olhado nada.
public sealed record ModuleUnderTest(string Name, Assembly Internal, bool HasDomain = false)
{
    internal string EntryTypeFullName => $"{Name}.{Name}Module";

    public override string ToString() => Name;
}

internal static class Modules
{
    // Antes de `Real`, que a usa: propriedades estáticas inicializam na ordem do arquivo.
    internal static ModuleUnderTest Ledger { get; } = new("Ledger", typeof(Ledger.LedgerModule).Assembly, HasDomain: true);

    internal static IReadOnlyList<ModuleUnderTest> Real { get; } =
    [
        Ledger,
        new("Positions", typeof(Positions.PositionsModule).Assembly),
        new("Customers", typeof(Customers.CustomersModule).Assembly, HasDomain: true),
    ];

    internal static ModuleUnderTest FixtureAlpha { get; } = new("FixtureAlpha", typeof(FixtureAlpha.FixtureAlphaModule).Assembly);

    internal static ModuleUnderTest FixtureBeta { get; } = new("FixtureBeta", typeof(FixtureBeta.FixtureBetaModule).Assembly);
}
