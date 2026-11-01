using BuildingBlocks.Persistence;
using Ledger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Ledger.Infrastructure.Outbox;

// Apaga em lotes os eventos processados há mais de 7 dias; roda no ciclo da limpeza de chaves (ADR-0035).
// Uma falha só adia a limpeza para a rodada seguinte.
internal sealed partial class OutboxPurge(ILogger<OutboxPurge> logger)
{
    internal async Task PurgeAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            var (removed, batches) = await BatchedPurge.RunAsync(db, "ledger.purge_processed_outbox", cancellationToken);
            LogPurged(removed, batches);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox purge removed {Count} processed events in {Batches} batches")]
    private partial void LogPurged(long count, int batches);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox purge failed")]
    private partial void LogFailed(Exception exception);
}
