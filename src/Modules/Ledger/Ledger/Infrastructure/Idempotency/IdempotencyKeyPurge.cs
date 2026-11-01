using BuildingBlocks.Persistence;
using Ledger.Infrastructure.Outbox;
using Ledger.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledger.Infrastructure.Idempotency;

// Apaga as chaves com mais de 7 dias, em lotes, na subida e a cada intervalo (ADR-0037); no mesmo ciclo, os eventos
// processados do outbox (ADR-0035). Uma falha só adia a limpeza para a rodada seguinte: o Worker segue de pé.
internal sealed partial class IdempotencyKeyPurge(
    IServiceScopeFactory scopes, TimeSpan interval, OutboxPurge outboxPurge, ILogger<IdempotencyKeyPurge> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogScheduled(interval);
        using var timer = new PeriodicTimer(interval);
        do
        {
            await PurgeAsync(stoppingToken);
            await PurgeOutboxAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var (removed, batches) = await BatchedPurge.RunAsync(
                scope.ServiceProvider.GetRequiredService<LedgerDbContext>(), "ledger.purge_expired_idempotency_keys", cancellationToken);
            LogPurged(removed, batches);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(exception);
        }
    }

    private async Task PurgeOutboxAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await outboxPurge.PurgeAsync(scope.ServiceProvider.GetRequiredService<LedgerDbContext>(), cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotency key purge scheduled every {Interval}")]
    private partial void LogScheduled(TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotency key purge removed {Count} expired keys in {Batches} batches")]
    private partial void LogPurged(long count, int batches);

    [LoggerMessage(Level = LogLevel.Error, Message = "Idempotency key purge failed")]
    private partial void LogFailed(Exception exception);
}
