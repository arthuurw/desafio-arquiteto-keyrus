using System.Reflection;
using Mediator;

namespace ArchitectureTests.Rules;

// Regra do ADR-0032: todo comando passa pelo behavior de idempotência, salvo as exceções nomeadas.
internal static class IdempotentCommandRules
{
    internal static IReadOnlyList<string> Violations(Assembly assembly, Type idempotentCommand, params Type[] exempt) =>
        Commands(assembly)
            .Where(type => !idempotentCommand.IsAssignableFrom(type) && !exempt.Contains(type))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToList();

    internal static IReadOnlyList<string> Protected(Assembly assembly, Type idempotentCommand) =>
        Commands(assembly)
            .Where(idempotentCommand.IsAssignableFrom)
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<Type> Commands(Assembly assembly) =>
        assembly.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IBaseCommand).IsAssignableFrom(type));
}
