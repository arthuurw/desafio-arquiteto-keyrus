using System.Reflection;
using BuildingBlocks.Logging;
using Microsoft.Extensions.Logging;

namespace ArchitectureTests.Rules;

// Regras do ADR-0016 e do ADR-0017: nome, CPF, nascimento e e-mail não saem de Customers, nem por um contrato dele,
// e não vão para o log sem a classificação que os apaga.
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

    // Tipos de domínio de Customers e nomes de parâmetro que carregam nome, CPF, nascimento ou e-mail.
    private static readonly string[] PersonalTypes = ["CustomerName", "Cpf", "BirthDate", "Email"];
    private static readonly string[] PersonalParameters = ["name", "cpf", "birthDate", "email"];

    // Parâmetros de método de log com dado pessoal sem `[PersonalData]`: sem a marca, o valor sai em claro (ADR-0041).
    internal static IReadOnlyList<string> UnmarkedLogParameters(params Assembly[] assemblies) =>
        assemblies
            .SelectMany(assembly => assembly.GetTypes().Where(type => OwnedBy(type, assembly)))
            .SelectMany(type => type.GetMethods(Declared))
            .Where(method => method.GetCustomAttribute<LoggerMessageAttribute>() is not null)
            .SelectMany(method => method.GetParameters()
                .Where(parameter => IsPersonal(parameter) && parameter.GetCustomAttribute<PersonalDataAttribute>() is null)
                .Select(parameter => $"{method.DeclaringType!.FullName}.{method.Name}({parameter.Name})"))
            .Order(StringComparer.Ordinal)
            .ToList();

    // Membros marcados `[PersonalData]`: propriedades, campos e parâmetros de construtor, como os de um record posicional.
    internal static IReadOnlyList<string> MarkedMembers(params Assembly[] assemblies) =>
        assemblies
            .SelectMany(assembly => assembly.GetTypes().Where(type => OwnedBy(type, assembly)))
            .SelectMany(type => type.GetProperties(Declared).Where(Marked).Select(property => $"{type.FullName}.{property.Name}")
                .Concat(type.GetFields(Declared).Where(Marked).Select(field => $"{type.FullName}.{BackingName(field.Name)}"))
                .Concat(type.GetConstructors(Declared).SelectMany(constructor => constructor.GetParameters())
                    .Where(parameter => parameter.GetCustomAttribute<PersonalDataAttribute>() is not null)
                    .Select(parameter => $"{type.FullName}({parameter.Name})")))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    private static bool IsPersonal(ParameterInfo parameter) =>
        PersonalTypes.Contains(parameter.ParameterType.Name, StringComparer.Ordinal)
        || PersonalParameters.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase);

    private static bool Marked(MemberInfo member) => member.GetCustomAttribute<PersonalDataAttribute>() is not null;

    private static bool OwnedBy(Type type, Assembly assembly) =>
        type.Namespace is { } ns && (ns == assembly.GetName().Name || ns.StartsWith($"{assembly.GetName().Name}.", StringComparison.Ordinal));

    // `<Cpf>k__BackingField` -> `Cpf`.
    private static string BackingName(string field) =>
        field.StartsWith('<') && field.IndexOf('>', StringComparison.Ordinal) is var end and > 0 ? field[1..end] : field;
}
