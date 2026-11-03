using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BuildingBlocks.Health;

// Readiness de cada host (ADR-0021): a dependência que não responde no prazo conta como fora, e a verificação nunca
// prende a requisição do orquestrador.
public static class Readiness
{
    public const string Route = "/health/ready";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public static IHealthChecksBuilder AddReadinessCheck<T>(this IHealthChecksBuilder builder, string name, HealthStatus failureStatus)
        where T : class, IHealthCheck =>
        builder.AddCheck<T>(name, failureStatus, tags: [], timeout: Timeout);
}
