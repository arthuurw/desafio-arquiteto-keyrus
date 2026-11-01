namespace Ledger.Contracts;

public enum ConsumeResult
{
    // Gravado agora: o evento era a próxima versão da conta.
    Applied,

    // Já estava no modelo de leitura: entrega repetida.
    AlreadyApplied,

    // Falta uma versão anterior; o evento volta na rodada seguinte.
    NotYet,
}

// Quem projeta os eventos do Ledger; o relay do outbox entrega cada evento por aqui (ADR-0035).
public interface IEntryRecordedConsumer
{
    Task<ConsumeResult> ConsumeAsync(EntryRecorded entry, CancellationToken cancellationToken);
}
