using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Positions.Infrastructure;

// `ping` no banco `positions` com o usuário do host, dentro do prazo do cliente MongoDB dele.
internal sealed class ReadModelHealthCheck(ReadModelStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await store.PingAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "MongoDB did not answer the ping.", exception);
        }
    }
}
