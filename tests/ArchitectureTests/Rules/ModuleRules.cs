using System.Text.RegularExpressions;
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace ArchitectureTests.Rules;

// Regras do ADR-0010. Cada função devolve as violações encontradas; lista vazia quando a regra passa.
internal static class ModuleRules
{
    // Dependências que o domínio não pode ter, além da infraestrutura do próprio módulo.
    private static readonly string[] ForbiddenInDomain =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "MongoDB",
        "Microsoft.AspNetCore",
    ];

    internal static IReadOnlyList<string> BoundaryViolations(IReadOnlyList<ModuleUnderTest> modules)
    {
        var architecture = Load(modules);
        var violations = new List<string>();
        foreach (var module in modules)
        {
            var others = modules.Where(other => other != module).Select(other => other.Internal).ToArray();
            var rule = Types().That().ResideInAssembly(module.Internal)
                .Should().NotDependOnAny(Types().That().ResideInAssembly(others[0], others[1..]));
            violations.AddRange(Failures(rule, architecture));
        }

        return violations;
    }

    internal static IReadOnlyList<string> DomainViolations(IReadOnlyList<ModuleUnderTest> modules)
    {
        var architecture = Load(modules);
        var violations = new List<string>();
        foreach (var module in modules)
        {
            var forbidden = ForbiddenInDomain.Append($"{module.Name}.Infrastructure").Select(Regex.Escape);
            var rule = Types().That().ResideInNamespaceMatching($@"^{Regex.Escape(module.Name)}\.Domain(\..+)?$")
                .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching($@"^({string.Join('|', forbidden)})(\..+)?$")
                // Módulo ainda sem domínio passa; a fixture prova que a regra reprova quando há tipos.
                .WithoutRequiringPositiveResults();
            violations.AddRange(Failures(rule, architecture));
        }

        return violations;
    }

    internal static IReadOnlyList<string> VisibilityViolations(IReadOnlyList<ModuleUnderTest> modules)
    {
        var architecture = Load(modules);
        var violations = new List<string>();
        foreach (var module in modules)
        {
            var rule = Types().That().ResideInAssembly(module.Internal).And().ArePublic()
                .Should().HaveFullName(module.EntryTypeFullName);
            violations.AddRange(Failures(rule, architecture));
        }

        return violations;
    }

    private static Architecture Load(IReadOnlyList<ModuleUnderTest> modules) =>
        new ArchLoader().LoadAssemblies(modules.Select(module => module.Internal).ToArray()).Build();

    private static IEnumerable<string> Failures(IArchRule rule, Architecture architecture) =>
        rule.Evaluate(architecture).Where(result => !result.Passed).Select(result => result.Description);
}
