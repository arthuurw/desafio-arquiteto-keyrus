using Ledger.Contracts;
using Positions.Infrastructure;

namespace Positions.Projections;

// Projeção pela próxima versão estrita (ADR-0035): o evento entra só quando é a versão projetada + 1. Menor ou igual já
// entrou; maior espera a anterior, que outra réplica pode estar entregando. Correta com qualquer número de réplicas.
internal sealed class PositionProjection(ReadModelStore store, ReadModelIndexes indexes) : IEntryRecordedConsumer
{
    public async Task<ConsumeResult> ConsumeAsync(EntryRecorded entry, CancellationToken cancellationToken)
    {
        await indexes.EnsureAsync(cancellationToken);

        var projected = (await store.PositionAsync(entry.AccountId, cancellationToken)).Version;
        var result = Decide(entry.Sequence, projected);
        if (result != ConsumeResult.Applied)
        {
            return result;
        }

        // Lançamento antes da versão: uma queda entre os dois passos deixa o lançamento gravado e a versão parada, e a
        // nova entrega regrava o mesmo documento e avança. Leitores só veem lançamentos até a versão.
        await store.UpsertEntryAsync(entry, cancellationToken);
        if (await store.TryAdvanceAsync(entry.AccountId, projected, entry.Sequence, entry.BalanceAfter, cancellationToken))
        {
            return ConsumeResult.Applied;
        }

        // Outra entrega moveu a versão entre a leitura e a gravação.
        var current = (await store.PositionAsync(entry.AccountId, cancellationToken)).Version;
        return Decide(entry.Sequence, current) == ConsumeResult.AlreadyApplied ? ConsumeResult.AlreadyApplied : ConsumeResult.NotYet;
    }

    private static ConsumeResult Decide(long sequence, long projected) =>
        sequence <= projected ? ConsumeResult.AlreadyApplied
        : sequence == projected + 1 ? ConsumeResult.Applied
        : ConsumeResult.NotYet;
}

// Cria os índices uma vez por processo, na primeira entrega; uma falha tenta de novo na entrega seguinte. Criar um
// índice que já existe não muda nada, então duas entregas simultâneas não precisam de exclusão.
internal sealed class ReadModelIndexes(ReadModelStore store)
{
    private volatile bool _created;

    internal async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (!_created)
        {
            await store.EnsureIndexesAsync(cancellationToken);
            _created = true;
        }
    }
}
