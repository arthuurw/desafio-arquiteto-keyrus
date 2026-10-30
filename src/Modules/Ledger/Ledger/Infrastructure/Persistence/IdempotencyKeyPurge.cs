using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledger.Infrastructure.Persistence;

// Apaga as chaves com mais de 7 dias na subida e a cada intervalo (ADR-0014). Uma falha só adia a limpeza
// para a rodada seguinte: o Worker segue de pé.
internal sealed partial class IdempotencyKeyPurge(
    IServiceScopeFactory scopes, TimeSpan interval, ILogger<IdempotencyKeyPurge> logger) : BackgroundService
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogScheduled(interval);
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
            var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
            // A função do migrator é o único caminho de DELETE da aplicação, e o prazo vive nela (ADR-0015).
            var purged = await db.Database
                .SqlQuery<long>($"SELECT ledger.purge_expired_idempotency_keys() AS \"Value\"")
                .SingleAsync(cancellationToken);
            LogPurged(purged);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotency key purge scheduled every {Interval}")]
    private partial void LogScheduled(TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotency key purge removed {Count} expired keys")]
    private partial void LogPurged(long count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Idempotency key purge failed")]
    private partial void LogFailed(Exception exception);
}
