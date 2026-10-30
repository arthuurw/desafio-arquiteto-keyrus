using Microsoft.IdentityModel.JsonWebTokens;

namespace IntegrationTests.Identity;

internal static class TokenClaims
{
    // Escopos do token, ordenados para comparar conjuntos.
    internal static string[] Scopes(this JsonWebToken token) =>
        token.GetClaim("scope").Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
}
