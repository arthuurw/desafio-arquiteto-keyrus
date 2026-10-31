using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IntegrationTests.Ledger;

// Roda a ação armada pelo teste logo antes de o behavior ler a chave já registrada: põe a limpeza entre o conflito
// do INSERT e o SELECT, janela que nenhuma espera do banco alcança. Desarmado, não faz nada.
internal sealed class KeyReadInterceptor : DbCommandInterceptor
{
    private Guid _key;
    private Func<Task>? _action;

    internal int Fired { get; private set; }

    internal void Arm(Guid key, Func<Task> action)
    {
        _key = key;
        _action = action;
        Fired = 0;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (_action is { } action
            && command.CommandText.Contains("FROM ledger.idempotency_keys", StringComparison.Ordinal)
            && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid key && key == _key))
        {
            _action = null;
            Fired++;
            await action();
        }

        return result;
    }
}
