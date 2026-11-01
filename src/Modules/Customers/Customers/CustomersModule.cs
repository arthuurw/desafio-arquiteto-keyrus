using BuildingBlocks.Audit;
using BuildingBlocks.Idempotency;
using Customers.Contracts;
using Customers.Features;
using Customers.Features.ReadCustomer;
using Customers.Features.RegisterCustomer;
using Customers.Infrastructure.Idempotency;
using Customers.Infrastructure.Persistence;
using Customers.Infrastructure.Seed;
using Customers.Infrastructure.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Customers;

// Único tipo público do módulo: ponto de composição usado pelos hosts (ADR-0031).
public static class CustomersModule
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection services, IConfiguration configuration)
    {
        AddDatabase(services, configuration);

        // Namespace próprio: cada módulo gera o seu mediador, e os endpoints o alcançam só pela facade (ADR-0036).
        services.AddMediator(options =>
        {
            options.Namespace = "Customers.Mediation";
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.GenerateTypesAsInternal = true;
            // Restrito ao IIdempotentCommand de Customers: nunca envolve um comando do Ledger (ADR-0037).
            options.PipelineBehaviors = [typeof(IdempotencyBehavior<,>)];
        });
        services.AddScoped<ICustomersCommands, CustomersCommands>();
        // O registro de auditoria da requisição, aberto pelo middleware e gravado na transação da escrita (ADR-0041).
        services.TryAddScoped<AuditEvent>();

        // Cofre lido na subida: endereço ou credencial ausente derruba a Api antes de servir, não no primeiro cadastro.
        var vault = OpenBaoSettings.Read(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(vault);
        // Um cliente só, com o token do AppRole guardado entre as requisições; a conexão é renovada a cada 2 min.
        services.AddSingleton(provider => new OpenBaoClient(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) }) { Timeout = OpenBaoSettings.Timeout },
            vault,
            provider.GetRequiredService<TimeProvider>()));
        services.AddTransient<IKeyStore, OpenBaoKeyStore>();
        services.AddTransient<IBlindIndexer, OpenBaoBlindIndexer>();
        services.AddSingleton(new DekCache(DekCache.ReadTtl(configuration)));
        services.AddTransient<CustomerKeys>();
        services.AddScoped<CustomerRegistration>();
        services.AddScoped<CustomerReader>();

        // Contrato público para os outros módulos, respondido só pelo PostgreSQL (ADR-0031).
        services.AddScoped<ICustomerQueries, CustomerQueries>();

        if (configuration.GetValue<bool>(CustomerSeed.EnabledSetting))
        {
            services.AddHostedService<CustomerSeed>();
        }

        return services;
    }

    // Só o Worker limpa as chaves de idempotência, com a role `customers_worker` (ADR-0044).
    public static IServiceCollection AddCustomersIdempotencyKeyPurge(this IServiceCollection services, IConfiguration configuration)
    {
        AddDatabase(services, configuration);
        var interval = IdempotencyKeyPurgeInterval.Read(configuration);
        services.AddHostedService(provider => ActivatorUtilities.CreateInstance<IdempotencyKeyPurge>(provider, interval));
        return services;
    }

    public static IEndpointRouteBuilder MapCustomersEndpoints(this IEndpointRouteBuilder routes)
    {
        var customers = routes.MapGroup("/v1/customers").WithTags("Clientes");
        RegisterCustomerEndpoint.Map(customers);
        ReadCustomerEndpoints.Map(customers);
        return routes;
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

    private static void AddDatabase(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Customers")
            ?? throw new InvalidOperationException("ConnectionStrings:Customers is not configured.");

        services.AddDbContext<CustomersDbContext>(options =>
            options.UseNpgsql(connectionString, CustomersDbContext.ConfigureNpgsql));
    }
}
