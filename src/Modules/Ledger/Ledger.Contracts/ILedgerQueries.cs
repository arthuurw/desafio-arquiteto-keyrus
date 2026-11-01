using BuildingBlocks.Identity;

namespace Ledger.Contracts;

// Estado da conta lido no modelo de escrita: versão, saldo e o relógio do banco no instante da leitura.
public sealed record AccountSnapshot(Guid AccountId, long Version, decimal Balance, DateTimeOffset Now);

// Lançamento como o modelo de escrita o guarda.
public sealed record LedgerEntry(
    Guid Id,
    long Sequence,
    string Type,
    decimal Amount,
    decimal BalanceAfter,
    string Category,
    string ExternalReference,
    Guid? ReversedEntryId,
    DateTimeOffset RecordedAt);

// Página do extrato em `sequence` decrescente, até `MaxSequence` e abaixo de `BeforeSequence`; `[From, To)` em `recordedAt`.
public sealed record EntryPageQuery(
    Guid AccountId, long MaxSequence, long? BeforeSequence, DateTimeOffset? From, DateTimeOffset? To, int Limit);

// Leitura do Ledger para outros módulos, sem lock e sem expor as tabelas (ADR-0031, ADR-0007).
public interface ILedgerQueries
{
    // Null quando a conta não existe ou, no modo delegado, é de outro titular (ADR-0013).
    Task<AccountSnapshot?> AccountSnapshotAsync(Guid accountId, Caller caller, CancellationToken cancellationToken);

    // O último lançamento com `recordedAt <= at` e `sequence <= maxSequence`, ou null.
    Task<LedgerEntry?> LastEntryAtAsync(Guid accountId, DateTimeOffset at, long maxSequence, CancellationToken cancellationToken);

    Task<IReadOnlyList<LedgerEntry>> EntryPageAsync(EntryPageQuery query, CancellationToken cancellationToken);
}
