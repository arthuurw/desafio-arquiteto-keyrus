using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledger.Infrastructure.Persistence;

// Abre uma conexão com a role do host: sem o PostgreSQL, nem a Api registra nem o Worker entrega o outbox (ADR-0021).
internal sealed class LedgerDatabaseHealthCheck(LedgerDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus, "PostgreSQL did not accept a connection.");
}
