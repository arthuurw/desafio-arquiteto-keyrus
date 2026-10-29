using Ledger.Infrastructure.Persistence;
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

        return services;
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
