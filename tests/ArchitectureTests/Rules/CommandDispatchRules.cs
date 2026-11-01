using System.Reflection;
using Mediator;

namespace ArchitectureTests.Rules;

// Regra do ADR-0036: só a facade de comandos do módulo depende de um mediador; o código gerado no namespace
// `<Módulo>.Mediation` é a própria biblioteca e fica fora.
internal static class CommandDispatchRules
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Type[] Dispatchers = [typeof(IMediator), typeof(ISender), typeof(IPublisher)];

    internal static IReadOnlyList<string> Violations(Assembly assembly, string module, params string[] facades) =>
        assembly.GetTypes()
            .Where(type => !IsGenerated(type, module) && !facades.Contains(Outermost(type).FullName))
            .Where(type => DependsOnDispatcher(type, module))
            .Select(type => Outermost(type).FullName!)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    // Também os tipos aninhados que o compilador gera para lambdas: é ali que fica o parâmetro do endpoint.
    internal static bool DependsOnDispatcher(Type type, string module) =>
        UsedTypes(type).Any(used => IsDispatcher(used, module));

    private static IEnumerable<Type> UsedTypes(Type type)
    {
        foreach (var field in type.GetFields(Declared))
        {
            yield return field.FieldType;
        }

        foreach (var property in type.GetProperties(Declared))
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
        {
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }

            if (method is MethodInfo info)
            {
                yield return info.ReturnType;
            }

            foreach (var local in method.GetMethodBody()?.LocalVariables ?? [])
            {
                yield return local.LocalType;
            }
        }
    }

    private static bool IsDispatcher(Type type, string module)
    {
        var element = type.HasElementType ? type.GetElementType()! : type;
        return Dispatchers.Contains(element) || element.FullName == $"{module}.Mediation.Mediator";
    }

    private static bool IsGenerated(Type type, string module) =>
        Outermost(type).Namespace is { } ns && (ns == $"{module}.Mediation" || ns.StartsWith($"{module}.Mediation.", StringComparison.Ordinal))
        || Outermost(type).Namespace is "Microsoft.Extensions.DependencyInjection" or "Mediator";

    private static Type Outermost(Type type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type;
    }
}
