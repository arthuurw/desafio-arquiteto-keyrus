using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Customers.Infrastructure.Vault;

// Pronto só com o cofre inicializado e deslacrado: selado, ele responde, mas não entrega DEK nem HMAC (ADR-0050).
internal sealed class VaultHealthCheck(OpenBaoClient vault) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await vault.IsUnsealedAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus, "OpenBao is sealed or not initialized.");
        }
        catch (PersonalDataUnavailableException exception)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "OpenBao did not answer.", exception);
        }
    }
}
