using BuildingBlocks.Identity;
using Ledger.Contracts;
using Microsoft.Extensions.Logging;
using Positions.Infrastructure;

namespace Positions.Features;

// Garantia de frescor (ADR-0007, ADR-0035): a consulta lê a versão da conta no PostgreSQL e responde pelo MongoDB só
// quando a versão projetada é a mesma. Versão diferente, erro ou leitura acima do timeout respondem pelo Ledger, com
// os mesmos valores. A resposta é sempre um retrato até a versão lida, pelos dois caminhos.
internal sealed partial class FreshRead(ReadModelStore store, ILedgerQueries ledger, ReadTimeout timeout, ILogger<FreshRead> logger)
{
    // Versão, saldo e relógio da conta no Ledger, ou null: id fora do formato, conta inexistente ou, no modo
    // delegado, de outro titular respondem igual (ADR-0013).
    internal async Task<AccountSnapshot?> SnapshotAsync(string id, Caller caller, CancellationToken cancellationToken) =>
        Guid.TryParse(id, out var accountId) ? await ledger.AccountSnapshotAsync(accountId, caller, cancellationToken) : null;

    internal async Task<decimal> CurrentBalanceAsync(AccountSnapshot snapshot, CancellationToken cancellationToken)
    {
        var projected = await FromReadModelAsync(
            async token =>
            {
                var position = await store.PositionAsync(snapshot.AccountId, token);
                return position.Version == snapshot.Version ? new Hit<decimal>(position.Balance) : null;
            },
            cancellationToken);
        return projected?.Value ?? snapshot.Balance;
    }

    // Saldo depois do último lançamento até `at`, ou zero na versão 0 quando nenhum lançamento chega até lá.
    internal async Task<BalanceAt> BalanceAtAsync(AccountSnapshot snapshot, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var projected = await FromReadModelAsync(
            async token => await IsCurrentAsync(snapshot.AccountId, snapshot.Version, token)
                ? new Hit<LedgerEntry?>(await store.LastEntryAtAsync(snapshot.AccountId, ReadModelStore.Micros(at), snapshot.Version, token))
                : null,
            cancellationToken);
        var entry = projected is not null
            ? projected.Value
            : await ledger.LastEntryAtAsync(snapshot.AccountId, at, snapshot.Version, cancellationToken);
        return entry is null ? new BalanceAt(0m, 0) : new BalanceAt(entry.BalanceAfter, entry.Sequence);
    }

    internal async Task<IReadOnlyList<LedgerEntry>> EntryPageAsync(EntryPageQuery query, CancellationToken cancellationToken)
    {
        var projected = await FromReadModelAsync(
            async token => await IsCurrentAsync(query.AccountId, query.MaxSequence, token)
                ? new Hit<IReadOnlyList<LedgerEntry>>(await store.EntryPageAsync(query, token))
                : null,
            cancellationToken);
        return projected?.Value ?? await ledger.EntryPageAsync(query, cancellationToken);
    }

    private async Task<bool> IsCurrentAsync(Guid accountId, long version, CancellationToken cancellationToken) =>
        (await store.PositionAsync(accountId, cancellationToken)).Version == version;

    // Null quando o MongoDB não serve: defasado, com erro ou acima do timeout. O timeout não depende do driver honrar
    // o cancelamento: a consulta segue sem esperar a leitura abandonada.
    private async Task<Hit<T>?> FromReadModelAsync<T>(Func<CancellationToken, Task<Hit<T>?>> read, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout.Value);
        var reading = read(deadline.Token);
        var finished = await Task.WhenAny(reading, Task.Delay(timeout.Value, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (finished != reading)
        {
            // A leitura abandonada ainda pode falhar; a falha é observada aqui para não virar exceção não tratada.
            _ = reading.ContinueWith(task => task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            LogTimedOut(timeout.Value);
            return null;
        }

        try
        {
            var hit = await reading;
            if (hit is null)
            {
                LogBehind();
            }
            else
            {
                LogServed();
            }

            return hit;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // O prazo e a espera vencem juntos: a leitura cancelada pelo prazo também é tempo esgotado.
            LogTimedOut(timeout.Value);
            return null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailed(exception);
            return null;
        }
    }

    // Valor lido no modelo de leitura em dia; um `null` dentro dele é resposta, não falta de leitura.
    private sealed record Hit<T>(T Value);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Answering from the read model")]
    private partial void LogServed();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Read model behind the ledger; answering from the ledger")]
    private partial void LogBehind();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Read model did not answer within {Timeout}; answering from the ledger")]
    private partial void LogTimedOut(TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Read model failed; answering from the ledger")]
    private partial void LogFailed(Exception exception);
}
