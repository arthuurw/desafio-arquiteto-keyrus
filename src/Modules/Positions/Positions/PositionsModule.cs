using BuildingBlocks.Configuration;
using BuildingBlocks.Health;
using Ledger.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Positions.Features;
using Positions.Features.GetBalance;
using Positions.Features.GetStatement;
using Positions.Infrastructure;
using Positions.Projections;

namespace Positions;

// Único tipo público do módulo: ponto de composição usado pelos hosts (ADR-0052).
public static class PositionsModule
{
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MinReadTimeout = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaxReadTimeout = TimeSpan.FromSeconds(10);

    // A projeção pode esperar o MongoDB mais que a consulta: o relay tenta de novo depois.
    private static readonly TimeSpan ProjectionTimeout = TimeSpan.FromSeconds(5);

    // Consultas da Api, com o usuário que só lê o banco `positions` (ADR-0056).
    public static IServiceCollection AddPositionsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var timeout = TimeSpanSetting.Read(configuration, "Positions:ReadTimeout", DefaultReadTimeout, MinReadTimeout, MaxReadTimeout);
        var connectionString = ConnectionString(configuration);
        services.AddSingleton(new ReadTimeout(timeout));
        services.AddSingleton(_ => new ReadModelStore(ReadModelStore.CreateClient(connectionString, timeout)));
        services.AddScoped<FreshRead>();
        return services;
    }

    // Projeção do Worker, com o usuário que grava no banco `positions` (ADR-0056).
    public static IServiceCollection AddPositionsProjection(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = ConnectionString(configuration);
        services.AddSingleton(_ => new ReadModelStore(ReadModelStore.CreateClient(connectionString, ProjectionTimeout)));
        services.AddSingleton<ReadModelIndexes>();
        services.AddScoped<IEntryRecordedConsumer, PositionProjection>();
        return services;
    }

    // Na Api, o MongoDB fora deixa a readiness `Degraded`, não fora: as consultas seguem pelo PostgreSQL, e tirar todas as
    // réplicas do balanceador faria da falha parcial uma total. No Worker, sem ele não há projeção (ADR-0021).
    public static IHealthChecksBuilder AddPositionsReadModelCheck(this IHealthChecksBuilder builder, HealthStatus failureStatus) =>
        builder.AddReadinessCheck<ReadModelHealthCheck>("mongodb", failureStatus);

    public static IEndpointRouteBuilder MapPositionsEndpoints(this IEndpointRouteBuilder routes)
    {
        var accounts = routes.MapGroup("/v1/accounts").WithTags("Posições");
        GetBalanceEndpoint.Map(accounts);
        GetStatementEndpoint.Map(accounts);
        return routes;
    }

    private static string ConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString("Positions")
        ?? throw new InvalidOperationException("ConnectionStrings:Positions is not configured.");
}
