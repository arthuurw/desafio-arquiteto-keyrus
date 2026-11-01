using Positions.Infrastructure;

namespace Positions.Projections;

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
