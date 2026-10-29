using Customers;
using Ledger;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Migrator;

// Aplica as migrações de cada módulo com a role de migração e encerra (ADR-0019).
// Código de saída: 0 em sucesso, 1 em qualquer falha.
var builder = Host.CreateApplicationBuilder(args);
using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migrator");

var modules = new (string Name, Func<string, CancellationToken, Task<IReadOnlyList<string>>> Migrate)[]
{
    ("Ledger", LedgerModule.MigrateAsync),
    ("Customers", CustomersModule.MigrateAsync),
};

try
{
    var connectionString = builder.Configuration.GetConnectionString("Migrator")
        ?? throw new InvalidOperationException("ConnectionStrings:Migrator is not configured.");

    foreach (var module in modules)
    {
        var applied = await module.Migrate(connectionString, CancellationToken.None);
        if (applied.Count == 0)
        {
            logger.NoPendingMigrations(module.Name);
        }

        foreach (var migration in applied)
        {
            logger.AppliedMigration(module.Name, migration);
        }
    }

    return 0;
}
catch (Exception exception)
{
    logger.MigrationFailed(exception);
    return 1;
}
