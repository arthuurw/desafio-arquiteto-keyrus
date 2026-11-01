using System.Reflection;

namespace ArchitectureTests;

// Positions alcança o Ledger só pelo contrato público, e o Ledger não conhece o modelo de leitura (ADR-0031, ADR-0035).
public sealed class ModuleDependencyTests
{
    private static readonly Assembly Positions = typeof(Positions.PositionsModule).Assembly;
    private static readonly Assembly Ledger = typeof(Ledger.LedgerModule).Assembly;

    [Fact]
    public void PositionsReachesLedgerOnlyThroughContracts()
    {
        var references = ReferencedAssemblies(Positions);

        references.ShouldContain("Ledger.Contracts");
        references.ShouldNotContain("Ledger");
        references.ShouldNotContain(name => name.StartsWith("Npgsql", StringComparison.Ordinal));
        references.ShouldNotContain(name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Fact]
    public void LedgerDoesNotKnowTheReadModel()
    {
        var references = ReferencedAssemblies(Ledger);

        references.ShouldContain("Ledger.Contracts");
        references.ShouldNotContain("Positions");
        references.ShouldNotContain(name => name.StartsWith("MongoDB", StringComparison.Ordinal));
    }

    private static List<string> ReferencedAssemblies(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(name => name.Name!).ToList();
}
