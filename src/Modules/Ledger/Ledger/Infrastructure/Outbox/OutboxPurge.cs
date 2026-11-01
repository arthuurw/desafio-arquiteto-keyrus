using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ledger.Infrastructure.Outbox;

// Apaga em lotes os eventos processados há mais de 7 dias; roda no ciclo da limpeza de chaves (ADR-0035).
// Uma falha só adia a limpeza para a rodada seguinte.
internal sealed partial class OutboxPurge(ILogger<OutboxPurge> logger)
{
    // Cada chamada fica bem abaixo do timeout de comando, qualquer que seja o acúmulo.
    private const int BatchSize = 1000;

    internal async Task PurgeAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            long total = 0;
            var batches = 0;
            long purged;
            do
            {
                // O prazo vive na função do migrator; cada chamada confirma sozinha.
                purged = await db.Database
                    .SqlQuery<long>($"SELECT ledger.purge_processed_outbox({BatchSize}) AS \"Value\"")
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox purge removed {Count} processed events in {Batches} batches")]
    private partial void LogPurged(long count, int batches);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox purge failed")]
    private partial void LogFailed(Exception exception);
}
