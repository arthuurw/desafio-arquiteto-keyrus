using System.Security.Claims;

namespace BuildingBlocks.Identity;

public static class CallerPrincipalExtensions
{
    // A política de toda rota exige um chamador que resolve; chegar aqui sem ele é rota mapeada sem `RequireScope`.
    public static Caller ResolvedCaller(this ClaimsPrincipal user) =>
        Caller.FromPrincipal(user)
            ?? throw new InvalidOperationException("The route ran without a resolved caller; map it with RequireScope.");
}
