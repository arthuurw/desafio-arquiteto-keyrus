using Microsoft.AspNetCore.Authorization;

namespace BuildingBlocks.Identity;

// Exige o escopo por comparação exata; o token traz `scope` em uma única claim, separada por espaço (RFC 9068).
public sealed class ScopeRequirement(string scope) : AuthorizationHandler<ScopeRequirement>, IAuthorizationRequirement
{
    public string Scope { get; } = scope;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        var granted = context.User.FindFirst(IdentityClaims.Scope)?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

        if (granted.Contains(requirement.Scope, StringComparer.Ordinal))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
