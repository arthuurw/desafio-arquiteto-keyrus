using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Persistence;

// Chama uma função de limpeza do migrator em lotes até ela apagar menos que um lote. A função é o único caminho de
// DELETE da aplicação, e o prazo vive nela (ADR-0044); cada chamada confirma sozinha e fica bem abaixo do timeout de
// comando, qualquer que seja o acúmulo.
public static class BatchedPurge
{
    public const int BatchSize = 1000;

    // `function` é o nome qualificado fixo do módulo, nunca entrada; o tamanho do lote segue como parâmetro.
    public static async Task<(long Removed, int Batches)> RunAsync(DbContext db, string function, CancellationToken cancellationToken)
    {
        long removed = 0;
        var batches = 0;
        long purged;
        do
        {
            purged = await db.Database
                .SqlQuery<long>(FormattableStringFactory.Create($"SELECT {function}({{0}}) AS \"Value\"", BatchSize))
                .SingleAsync(cancellationToken);
            removed += purged;
            batches++;
        }
        while (purged == BatchSize);

        return (removed, batches);
    }
}
