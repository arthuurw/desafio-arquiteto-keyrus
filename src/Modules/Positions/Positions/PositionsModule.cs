using System.Globalization;
using Ledger.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Positions.Features;
using Positions.Features.GetBalance;
using Positions.Features.GetStatement;
using Positions.Infrastructure;
using Positions.Projections;

namespace Positions;

// Único tipo público do módulo: ponto de composição usado pelos hosts (ADR-0031).
public static class PositionsModule
{
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MinReadTimeout = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaxReadTimeout = TimeSpan.FromSeconds(10);

    // A projeção pode esperar o MongoDB mais que a consulta: o relay tenta de novo depois.
    private static readonly TimeSpan ProjectionTimeout = TimeSpan.FromSeconds(5);

    // Consultas da Api, com o usuário que só lê o banco `positions` (ADR-0035).
    public static IServiceCollection AddPositionsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var timeout = ReadTimeout(configuration);
        var connectionString = ConnectionString(configuration);
        services.AddSingleton(new ReadTimeout(timeout));
        services.AddSingleton(_ => new ReadModelStore(ReadModelStore.CreateClient(connectionString, timeout)));
        services.AddScoped<FreshRead>();
        return services;
    }

    // Projeção do Worker, com o usuário que grava no banco `positions` (ADR-0035).
    public static IServiceCollection AddPositionsProjection(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = ConnectionString(configuration);
        services.AddSingleton(_ => new ReadModelStore(ReadModelStore.CreateClient(connectionString, ProjectionTimeout)));
        services.AddSingleton<ReadModelIndexes>();
        services.AddScoped<IEntryRecordedConsumer, PositionProjection>();
        return services;
    }

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

    // Checado na subida: inválido, a Api cairia só na primeira consulta, já no ar.
    private static TimeSpan ReadTimeout(IConfiguration configuration)
    {
        const string Key = "Positions:ReadTimeout";
        var configured = configuration[Key];
        if (configured is null)
        {
            return DefaultReadTimeout;
        }

        if (!TimeSpan.TryParse(configured, CultureInfo.InvariantCulture, out var timeout) || timeout < MinReadTimeout || timeout > MaxReadTimeout)
        {
            throw new InvalidOperationException($"{Key} must be a time span between {MinReadTimeout:c} and {MaxReadTimeout:c}; got '{configured}'.");
        }

        return timeout;
    }
}
