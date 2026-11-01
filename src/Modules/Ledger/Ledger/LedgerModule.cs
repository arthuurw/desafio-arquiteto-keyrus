using BuildingBlocks.Configuration;
using Ledger.Contracts;
using Ledger.Features.ChangeCreditLimit;
using Ledger.Infrastructure.Idempotency;
using Ledger.Infrastructure.Outbox;
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
            // Restrito a IIdempotentCommand: só os comandos de escrita com chave passam por ele (ADR-0037).
            options.PipelineBehaviors = [typeof(IdempotencyBehavior<,>)];
        });

        // Contrato público para os outros módulos lerem o Ledger (ADR-0031).
        services.AddScoped<ILedgerQueries, LedgerQueries>();

        return services;
    }

    // Só o Worker limpa chaves de idempotência e eventos processados; a Api não carrega a rotina (ADR-0009).
    public static IServiceCollection AddLedgerIdempotencyKeyPurge(this IServiceCollection services, IConfiguration configuration)
    {
        var interval = TimeSpanSetting.Read(
            configuration, "Idempotency:PurgeInterval", IdempotencyKeyPurge.DefaultInterval, IdempotencyKeyPurge.MinInterval, IdempotencyKeyPurge.MaxInterval);
        services.AddSingleton<OutboxPurge>();
        services.AddHostedService(provider => ActivatorUtilities.CreateInstance<IdempotencyKeyPurge>(provider, interval));
        return services;
    }

    // Só o Worker entrega o outbox; o consumidor (`IEntryRecordedConsumer`) vem do módulo que projeta (ADR-0035).
    public static IServiceCollection AddLedgerOutboxRelay(this IServiceCollection services, IConfiguration configuration)
    {
        var interval = TimeSpanSetting.Read(
            configuration, "Outbox:PollInterval", OutboxRelay.DefaultPollInterval, OutboxRelay.MinPollInterval, OutboxRelay.MaxPollInterval);
        services.AddHostedService(provider => ActivatorUtilities.CreateInstance<OutboxRelay>(provider, interval));
        return services;
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
