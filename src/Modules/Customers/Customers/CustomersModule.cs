using Customers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Customers;

// Único tipo público do módulo: ponto de composição usado pelos hosts (ADR-0031).
public static class CustomersModule
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Customers")
            ?? throw new InvalidOperationException("ConnectionStrings:Customers is not configured.");

        services.AddDbContext<CustomersDbContext>(options =>
            options.UseNpgsql(connectionString, CustomersDbContext.ConfigureNpgsql));

        return services;
    }

    // Aplica as migrações pendentes e devolve os identificadores aplicados.
    public static async Task<IReadOnlyList<string>> MigrateAsync(string connectionString, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<CustomersDbContext>()
            .UseNpgsql(connectionString, CustomersDbContext.ConfigureNpgsql)
            .Options;

        await using var context = new CustomersDbContext(options);
        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await context.Database.MigrateAsync(cancellationToken);
        return pending;
    }
}
