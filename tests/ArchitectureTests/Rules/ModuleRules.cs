using System.Text.RegularExpressions;
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace ArchitectureTests.Rules;

// Regras do ADR-0031. Cada método devolve as violações encontradas; lista vazia quando a regra passa.
internal static class ModuleRules
{
    // Assemblies que o domínio não pode usar. Por assembly, e não por namespace:
    // Npgsql também expõe NpgsqlTypes, e o ASP.NET Core também expõe Microsoft.Net.Http.Headers.
    private static readonly string[] ForbiddenAssembliesInDomain =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "MongoDB",
        "Microsoft.AspNetCore",
        "Microsoft.Net.Http.Headers",
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
            var domain = $@"^{Regex.Escape(module.Name)}\.Domain(\..+)?$";
            var assemblies = string.Join('|', ForbiddenAssembliesInDomain.Select(Regex.Escape));

            var assemblyRule = Types().That().ResideInNamespaceMatching(domain)
                .Should().NotDependOnAnyTypesThat().ResideInAssemblyMatching($@"^({assemblies})(\.[^,]*)?(,.*)?$");
            var infrastructureRule = Types().That().ResideInNamespaceMatching(domain)
                .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching($@"^{Regex.Escape(module.Name)}\.Infrastructure(\..+)?$");

            // Módulo ainda sem domínio passa; a fixture prova que a regra reprova quando há tipos.
            violations.AddRange(Failures(module.HasDomain ? assemblyRule : assemblyRule.WithoutRequiringPositiveResults(), architecture));
            violations.AddRange(Failures(
                module.HasDomain ? infrastructureRule : infrastructureRule.WithoutRequiringPositiveResults(), architecture));
        }

        return violations;
    }

    // Os tipos que a regra de independência avalia no domínio do módulo.
    internal static IReadOnlyList<string> DomainTypes(ModuleUnderTest module) =>
        Load([module]).Types
            .Where(type => Regex.IsMatch(type.Namespace.FullName, $@"^{Regex.Escape(module.Name)}\.Domain(\..+)?$"))
            .Select(type => type.Name)
            .ToList();

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
