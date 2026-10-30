using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace BuildingBlocks.Identity;

public enum CallerMode
{
    Delegated,
    Service,
}

// Quem chama a Api: o client e, no modo delegado, o cliente final em nome de quem ele age (ADR-0013).
public sealed record Caller(string ClientId, CallerMode Mode, Guid? CustomerId)
{
    // O modo vem de marcas explícitas; qualquer outra combinação não resolve, para a falha ser fechada.
    public static Caller? FromPrincipal(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var customerIds = user.FindAll(IdentityClaims.CustomerId).ToList();
        var clientIds = user.FindAll(IdentityClaims.ClientId).ToList();

        if (customerIds.Count == 1 && clientIds.Count == 0)
        {
            var authorizedParty = user.FindFirst(IdentityClaims.AuthorizedParty)?.Value;
            return Guid.TryParseExact(customerIds[0].Value, "D", out var customerId) && !string.IsNullOrEmpty(authorizedParty)
                ? new Caller(authorizedParty, CallerMode.Delegated, customerId)
                : null;
        }

        if (clientIds.Count == 1 && customerIds.Count == 0 && !string.IsNullOrEmpty(clientIds[0].Value))
        {
            return new Caller(clientIds[0].Value, CallerMode.Service, null);
        }

        return null;
    }

    // Permite declarar o chamador como parâmetro do handler; a política da rota já garantiu que ele resolve.
    public static ValueTask<Caller?> BindAsync(HttpContext context) => ValueTask.FromResult(FromPrincipal(context.User));
}
