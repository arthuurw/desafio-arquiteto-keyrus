using System.Reflection;
using System.Xml.Linq;

namespace ArchitectureTests;

// A identidade do chamador fica num projeto só com a BCL, e os contratos dos módulos não dependem de mais nada (ADR-0052).
public sealed class IdentityProjectTests
{
    private const string Identity = "BuildingBlocks.Identity";

    private static readonly string[] SourceAssemblies =
    [
        "Api", "Worker", "Migrator", "BuildingBlocks", Identity,
        "Ledger", "Ledger.Contracts", "Positions", "Positions.Contracts", "Customers", "Customers.Contracts",
    ];

    public static TheoryData<string> Contracts { get; } = ["Ledger.Contracts", "Positions.Contracts", "Customers.Contracts"];

    [Fact]
    public void HasNoDependencies()
    {
        var project = XDocument.Load(Path.Combine(RepositoryRoot(), "src", Identity, $"{Identity}.csproj"));
        var references = project.Descendants()
            .Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference" or "FrameworkReference")
            .Select(element => $"{element.Name.LocalName} {element.Attribute("Include")?.Value}")
            .ToList();

        references.ShouldBeEmpty();
        ReferencedAssemblies(Identity).Where(name => !IsSystem(name)).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Contracts))]
    public void ContractsReferenceOnlyIdentity(string contract)
    {
        var references = ReferencedAssemblies(contract);

        references.Where(name => !IsSystem(name)).ShouldBeSubsetOf([Identity]);
        references.ShouldNotContain("BuildingBlocks");
        references.ShouldNotContain(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        references.ShouldNotContain(name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        references.ShouldNotContain(name => name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal));
    }

    // O Ledger.Contracts usa `Caller` na consulta de snapshot: a referência existe e é a única fora da BCL.
    [Fact]
    public void LedgerContractsReferenceIdentity() =>
        ReferencedAssemblies("Ledger.Contracts").Where(name => !IsSystem(name)).ShouldBe([Identity]);

    [Fact]
    public void CallerTypesLiveOnlyInIdentity()
    {
        string[] names = ["Caller", "CallerMode", "Scopes"];

        var definitions = SourceAssemblies
            .SelectMany(assembly => Assembly.Load(new AssemblyName(assembly)).GetTypes().Select(type => (Assembly: assembly, Type: type)))
            .Where(definition => names.Contains(definition.Type.Name))
            .Select(definition => $"{definition.Assembly}: {definition.Type.FullName}")
            .Order(StringComparer.Ordinal)
            .ToList();

        definitions.ShouldBe(
        [
            $"{Identity}: BuildingBlocks.Identity.Caller",
            $"{Identity}: BuildingBlocks.Identity.CallerMode",
            $"{Identity}: BuildingBlocks.Identity.Scopes",
        ]);
    }

    private static List<string> ReferencedAssemblies(string assembly) =>
        Assembly.Load(new AssemblyName(assembly)).GetReferencedAssemblies().Select(name => name.Name!).ToList();

    private static bool IsSystem(string name) => name.StartsWith("System", StringComparison.Ordinal) || name == "netstandard";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ledger.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Ledger.slnx not found above the test output directory.");
    }
}
