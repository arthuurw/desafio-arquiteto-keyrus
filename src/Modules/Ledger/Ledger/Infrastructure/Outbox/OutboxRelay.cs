using Ledger.Contracts;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledger.Infrastructure.Outbox;

// Entrega os eventos pendentes ao consumidor, em lotes, por polling (ADR-0035). Cada lote é uma transação: reserva com
// `SKIP LOCKED`, entrega em ordem de `id` e marca os aplicados e os já aplicados. O "ainda não" e o malformado seguem
// pendentes; um lote cheio só deles move o cursor para depois dele, e o fim da fila o devolve ao início. Uma falha do
// consumidor desfaz o lote inteiro, que volta depois de uma espera crescente.
internal sealed partial class OutboxRelay(
    IServiceScopeFactory scopes, TimeSpan pollInterval, ILogger<OutboxRelay> logger) : BackgroundService
{
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(1);

    private const int BatchSize = 100;
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogScheduled(pollInterval);
        var failures = 0;
        long after = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                var batch = await RelayBatchAsync(after, stoppingToken);
                failures = 0;
                // Lote cheio: há mais pendentes, e o próximo lote sai sem esperar. Sem nada marcado, ele começa depois
                // deste; com algo marcado, do mesmo ponto, onde os marcados abriram espaço.
                if (batch.Claimed == BatchSize)
                {
                    after = batch.Progressed ? after : batch.LastId;
                    continue;
                }

                after = 0;
                wait = pollInterval;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                failures++;
                wait = Backoff(failures);
                LogFailed(exception, wait);
            }

            await Task.Delay(wait, stoppingToken);
        }
    }

    private async Task<(int Claimed, bool Progressed, long LastId)> RelayBatchAsync(long after, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var consumer = scope.ServiceProvider.GetRequiredService<IEntryRecordedConsumer>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await db.Database
            .SqlQuery<ClaimedEvent>(
                $"""SELECT id AS "Id", event_type AS "EventType", payload::text AS "Payload" FROM ledger.claim_outbox_batch({BatchSize}, {after})""")
            .ToListAsync(cancellationToken);

        var done = new List<long>();
        int applied = 0, alreadyApplied = 0, deferred = 0, malformed = 0;
        foreach (var claimedEvent in claimed)
        {
            var entry = OutboxEvent.Read(claimedEvent.EventType, claimedEvent.Payload);
            if (entry is null)
            {
                // Fica pendente sem segurar os outros: o lote segue com os eventos válidos.
                malformed++;
                LogMalformed(claimedEvent.Id, claimedEvent.EventType);
                continue;
            }

            switch (await consumer.ConsumeAsync(entry, cancellationToken))
            {
                case ConsumeResult.Applied:
                    applied++;
                    done.Add(claimedEvent.Id);
                    break;
                case ConsumeResult.AlreadyApplied:
                    alreadyApplied++;
                    done.Add(claimedEvent.Id);
                    break;
                default:
                    deferred++;
                    break;
            }
        }

        if (done.Count > 0)
        {
            await db.Database
                .SqlQuery<long>($"SELECT ledger.mark_outbox_processed({done.ToArray()}) AS \"Value\"")
                .SingleAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (claimed.Count > 0)
        {
            LogBatch(claimed.Count, applied, alreadyApplied, deferred, malformed);
        }

        return (claimed.Count, done.Count > 0, claimed.Count > 0 ? claimed[^1].Id : after);
    }

    // 1 s, 2 s, 4 s... até 30 s.
    private static TimeSpan Backoff(int failures) =>
        TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, FirstBackoff.Ticks << Math.Min(failures - 1, 10)));

    private sealed record ClaimedEvent(long Id, string EventType, string Payload);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox relay polling every {Interval}")]
    private partial void LogScheduled(TimeSpan interval);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Outbox relay claimed {Claimed} events: {Applied} applied, {AlreadyApplied} already applied, {Deferred} deferred, {Malformed} malformed")]
    private partial void LogBatch(int claimed, int applied, int alreadyApplied, int deferred, int malformed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox event {Id} of type {EventType} is not a valid EntryRecorded.v1 and stays pending")]
    private partial void LogMalformed(long id, string eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox relay failed; retrying in {Wait}")]
    private partial void LogFailed(Exception exception, TimeSpan wait);
}
