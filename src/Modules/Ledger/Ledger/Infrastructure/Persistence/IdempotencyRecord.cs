namespace Ledger.Infrastructure.Persistence;

// Linha de `idempotency_keys`, mapeada só para a migração; o behavior lê e grava por SQL (ADR-0037).
internal sealed class IdempotencyRecord
{
    private IdempotencyRecord()
    {
        ClientId = null!;
        RequestHash = null!;
    }

    internal string ClientId { get; private set; }

    internal Guid Key { get; private set; }

    internal byte[] RequestHash { get; private set; }

    internal int? ResponseStatus { get; private set; }

    internal string? ResponseBody { get; private set; }

    internal DateTimeOffset CreatedAt { get; private set; }
}
