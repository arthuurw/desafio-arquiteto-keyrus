using BuildingBlocks.Persistence;
using Customers.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Customers.Infrastructure.Idempotency;

// Apaga as chaves de Customers com mais de 7 dias, em lotes, na subida e a cada intervalo, com a role
// `customers_worker` (ADR-0037, ADR-0038). Uma falha só adia a limpeza para a rodada seguinte.
internal sealed partial class IdempotencyKeyPurge(
    IServiceScopeFactory scopes, TimeSpan interval, ILogger<IdempotencyKeyPurge> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            await PurgeAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var (removed, batches) = await BatchedPurge.RunAsync(
                scope.ServiceProvider.GetRequiredService<CustomersDbContext>(), "customers.purge_expired_idempotency_keys", cancellationToken);
            LogPurged(removed, batches);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Customers idempotency key purge removed {Count} expired keys in {Batches} batches")]
    private partial void LogPurged(long count, int batches);

    [LoggerMessage(Level = LogLevel.Error, Message = "Customers idempotency key purge failed")]
    private partial void LogFailed(Exception exception);
}
