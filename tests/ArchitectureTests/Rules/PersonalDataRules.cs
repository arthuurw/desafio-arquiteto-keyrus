using System.Reflection;

namespace ArchitectureTests.Rules;

// Regra do ADR-0016: nome, CPF, nascimento e e-mail não saem de Customers, nem por um contrato dele.
internal static class PersonalDataRules
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly string[] PersonalData = ["Name", "Cpf", "BirthDate", "Email"];

    // Propriedades e campos com nome de dado pessoal; os campos de apoio que o compilador gera também contam.
    internal static IReadOnlyList<string> Violations(params Assembly[] assemblies) =>
        assemblies
            // Só os tipos do próprio módulo: o código que geradores emitem em `Microsoft.*` não é contrato de ninguém.
            .SelectMany(assembly => assembly.GetTypes().Where(type => OwnedBy(type, assembly)))
            .SelectMany(type => type.GetProperties(Declared).Select(property => (type, property.Name))
                .Concat(type.GetFields(Declared).Select(field => (type, Name: BackingName(field.Name)))))
            .Where(member => PersonalData.Contains(member.Name, StringComparer.Ordinal))
            .Select(member => $"{member.type.FullName}.{member.Name}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    private static bool OwnedBy(Type type, Assembly assembly) =>
        type.Namespace is { } ns && (ns == assembly.GetName().Name || ns.StartsWith($"{assembly.GetName().Name}.", StringComparison.Ordinal));

    // `<Cpf>k__BackingField` -> `Cpf`.
    private static string BackingName(string field) =>
        field.StartsWith('<') && field.IndexOf('>', StringComparison.Ordinal) is var end and > 0 ? field[1..end] : field;
}
