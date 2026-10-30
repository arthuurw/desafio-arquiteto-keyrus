using BuildingBlocks.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace ArchitectureTests.Rules;

// Negação por padrão (ADR-0013): todo endpoint declara um escopo ou acesso anônimo explícito.
// Endpoints de Minimal API são registrados em tempo de execução; a política é metadado, invisível ao ArchUnitNET.
internal static class EndpointPolicyRules
{
    internal static IReadOnlyList<string> Violations(IEnumerable<Endpoint> endpoints) =>
        endpoints
            .Where(endpoint => !DeclaresPolicy(endpoint))
            .Select(endpoint => endpoint.DisplayName ?? "(endpoint without name)")
            .ToList();

    private static bool DeclaresPolicy(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
        || endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>()
            .Any(policy => policy.Requirements.OfType<ScopeRequirement>().Any());
}
