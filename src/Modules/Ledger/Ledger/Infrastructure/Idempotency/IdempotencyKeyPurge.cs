using Ledger.Infrastructure.Outbox;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledger.Infrastructure.Idempotency;

// Apaga as chaves com mais de 7 dias, em lotes, na subida e a cada intervalo (ADR-0032); no mesmo ciclo, os eventos
// processados do outbox (ADR-0035). Uma falha só adia a limpeza para a rodada seguinte: o Worker segue de pé.
internal sealed partial class IdempotencyKeyPurge(
    IServiceScopeFactory scopes, TimeSpan interval, OutboxPurge outboxPurge, ILogger<IdempotencyKeyPurge> logger) : BackgroundService
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    // Cada chamada fica bem abaixo do timeout de comando, qualquer que seja o acúmulo.
    private const int BatchSize = 1000;

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
            var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
            long total = 0;
            var batches = 0;
            long purged;
            do
            {
                // A função do migrator é o único caminho de DELETE da aplicação, e o prazo vive nela (ADR-0033).
                // Cada chamada confirma sozinha: o lote seguinte começa do que sobrou.
                purged = await db.Database
                    .SqlQuery<long>($"SELECT ledger.purge_expired_idempotency_keys({BatchSize}) AS \"Value\"")
                    .SingleAsync(cancellationToken);
                total += purged;
                batches++;
            }
            while (purged == BatchSize);

            LogPurged(total, batches);
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
