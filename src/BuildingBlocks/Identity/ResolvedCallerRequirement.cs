using Microsoft.AspNetCore.Authorization;

namespace BuildingBlocks.Identity;

// Exige um chamador que resolve em um dos dois modos; combinação ambígua de claims é recusada com 403.
public sealed class ResolvedCallerRequirement : AuthorizationHandler<ResolvedCallerRequirement>, IAuthorizationRequirement
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ResolvedCallerRequirement requirement)
    {
        if (Caller.FromPrincipal(context.User) is not null)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
