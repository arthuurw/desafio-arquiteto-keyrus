using Microsoft.AspNetCore.Builder;

namespace BuildingBlocks.Audit;

// A trilha de um módulo: a tabela e o `DbContext` cuja role insere nela (ADR-0041).
public sealed record AuditTrail(string Table, Type DbContextType);

// O que a rota auditada registra: a operação, o tipo do recurso e o valor de rota com o id dele. Sem valor de rota, o id
// é o do recurso criado, lido do corpo do `201`.
public sealed record AuditedRoute(AuditTrail Trail, string Operation, string ResourceType, string? ResourceRouteValue);

public static class AuditedRoutes
{
    public static TBuilder Audited<TBuilder>(
        this TBuilder builder, AuditTrail trail, string operation, string resourceType, string? resourceRouteValue)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AuditedRoute(trail, operation, resourceType, resourceRouteValue));

    // Entre o tratador de exceção e a autenticação: vê também `401`, `403` e a exceção que vira `500`.
    public static IApplicationBuilder UseAuditTrail(this IApplicationBuilder app) => app.UseMiddleware<AuditMiddleware>();
}
