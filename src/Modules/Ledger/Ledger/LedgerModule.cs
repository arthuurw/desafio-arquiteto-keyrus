using System.Globalization;
using Ledger.Features.ChangeCreditLimit;
using Ledger.Infrastructure.Idempotency;
using Ledger.Features.OpenAccount;
using Ledger.Features.RecordEntry;
using Ledger.Features.ReverseEntry;
using Ledger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger;

// Único tipo público do módulo: ponto de composição usado pelos hosts (ADR-0031).
public static class LedgerModule
{
    public static IServiceCollection AddLedgerModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Ledger")
            ?? throw new InvalidOperationException("ConnectionStrings:Ledger is not configured.");

        services.AddDbContext<LedgerDbContext>(options =>
            options.UseNpgsql(connectionString, LedgerDbContext.ConfigureNpgsql));

        // Handlers com o tempo de vida do DbContext; tipos gerados internos, como o resto do módulo (ADR-0030).
        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.GenerateTypesAsInternal = true;
            // Restrito a IIdempotentCommand: só os comandos de escrita com chave passam por ele (ADR-0032).
            options.PipelineBehaviors = [typeof(IdempotencyBehavior<,>)];
        });

        return services;
    }

    // Só o Worker limpa chaves de idempotência; a Api não carrega a rotina (ADR-0009).
    public static IServiceCollection AddLedgerIdempotencyKeyPurge(this IServiceCollection services, IConfiguration configuration)
    {
        var interval = PurgeInterval(configuration);
        services.AddHostedService(provider => ActivatorUtilities.CreateInstance<IdempotencyKeyPurge>(provider, interval));
        return services;
    }

    // Checado na subida: inválido, o Worker cairia só no primeiro ciclo, já no ar.
    private static TimeSpan PurgeInterval(IConfiguration configuration)
    {
        const string Key = "Idempotency:PurgeInterval";
        var configured = configuration[Key];
        if (configured is null)
        {
            return IdempotencyKeyPurge.DefaultInterval;
        }

        if (!TimeSpan.TryParse(configured, CultureInfo.InvariantCulture, out var interval)
            || interval < IdempotencyKeyPurge.MinInterval
            || interval > IdempotencyKeyPurge.MaxInterval)
        {
            throw new InvalidOperationException(
                $"{Key} must be a time span between {IdempotencyKeyPurge.MinInterval:c} and {IdempotencyKeyPurge.MaxInterval:c}; got '{configured}'.");
        }

        return interval;
    }

    public static IEndpointRouteBuilder MapLedgerEndpoints(this IEndpointRouteBuilder routes)
    {
        var accounts = routes.MapGroup("/v1/accounts").WithTags("Contas");
        OpenAccountEndpoint.Map(accounts);
        ChangeCreditLimitEndpoint.Map(accounts);
        RecordEntryEndpoints.Map(accounts);
        ReverseEntryEndpoint.Map(accounts);
        return routes;
    }

    // Aplica as migrações pendentes e devolve os identificadores aplicados.
    public static async Task<IReadOnlyList<string>> MigrateAsync(string connectionString, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(connectionString, LedgerDbContext.ConfigureNpgsql)
            .Options;

        await using var context = new LedgerDbContext(options);
        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await context.Database.MigrateAsync(cancellationToken);
        return pending;
    }
}
