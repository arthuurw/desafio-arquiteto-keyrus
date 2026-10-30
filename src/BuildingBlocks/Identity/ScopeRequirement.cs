using Microsoft.AspNetCore.Authorization;

namespace BuildingBlocks.Identity;

// Exige o escopo por comparação exata e um chamador que resolve em um dos dois modos.
public sealed class ScopeRequirement(string scope) : AuthorizationHandler<ScopeRequirement>, IAuthorizationRequirement
{
    public string Scope { get; } = scope;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        // O Keycloak emite `scope` como uma lista separada por espaço; outros provedores, como várias claims.
        var granted = context.User.FindAll(IdentityClaims.Scope)
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (granted.Contains(requirement.Scope, StringComparer.Ordinal) && Caller.FromPrincipal(context.User) is not null)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
