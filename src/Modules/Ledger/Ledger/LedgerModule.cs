using Ledger.Features.ChangeCreditLimit;
using Ledger.Features.OpenAccount;
using Ledger.Features.RecordEntry;
using Ledger.Features.ReverseEntry;
using Ledger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger;

// Único tipo público do módulo: ponto de composição usado pelos hosts (ADR-0010).
public static class LedgerModule
{
    public static IServiceCollection AddLedgerModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Ledger")
            ?? throw new InvalidOperationException("ConnectionStrings:Ledger is not configured.");

        services.AddDbContext<LedgerDbContext>(options =>
            options.UseNpgsql(connectionString, LedgerDbContext.ConfigureNpgsql));

        // Handlers com o tempo de vida do DbContext; tipos gerados internos, como o resto do módulo (ADR-0008).
        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.GenerateTypesAsInternal = true;
        });

        return services;
    }

    public static IEndpointRouteBuilder MapLedgerEndpoints(this IEndpointRouteBuilder routes)
    {
        var accounts = routes.MapGroup("/v1/accounts");
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
