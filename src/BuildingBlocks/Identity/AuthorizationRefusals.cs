using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BuildingBlocks.Identity;

// Observador que a rota declara para saber das próprias recusas de autenticação (401) e de escopo (403), decididas antes
// do handler. Serve à rota cuja auditoria cobre todo acesso, também o recusado (ADR-0042).
public sealed record AuthorizationRefusalObserver(Action<HttpContext, int> Observe);

public static class AuthorizationRefusals
{
    public static TBuilder ObserveAuthorizationRefusals<TBuilder>(this TBuilder builder, Action<HttpContext, int> observe)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AuthorizationRefusalObserver(observe));
}

// Avisa o observador da rota e deixa a resposta com o tratador padrão, que chega aos eventos do JwtBearer. Se o
// observador falha, a exceção sobe e a recusa sai como `500`: o acesso continua negado, nunca liberado.
internal sealed class ObservedAuthorizationResultHandler(AuthorizationMiddlewareResultHandler inner) : IAuthorizationMiddlewareResultHandler
{
    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if ((authorizeResult.Challenged || authorizeResult.Forbidden)
            && context.GetEndpoint()?.Metadata.GetMetadata<AuthorizationRefusalObserver>() is { } observer)
        {
            observer.Observe(context, authorizeResult.Challenged ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden);
        }

        return inner.HandleAsync(next, context, policy, authorizeResult);
    }
}
