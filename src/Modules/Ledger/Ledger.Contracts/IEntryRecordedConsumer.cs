namespace Ledger.Contracts;

public enum ConsumeResult
{
    // Gravado agora: o evento era a próxima versão da conta.
    Applied,

    // Já estava no modelo de leitura: entrega repetida.
    AlreadyApplied,

    // Falta uma versão anterior; o evento volta na rodada seguinte.
    NotYet,

    // O modelo de leitura recusou a escrita deste evento, e não por estar fora do ar: ele fica pendente sem segurar os
    // outros do lote.
    Rejected,
}

// Quem projeta os eventos do Ledger; o relay do outbox entrega cada evento por aqui (ADR-0053).
public interface IEntryRecordedConsumer
{
    Task<ConsumeResult> ConsumeAsync(EntryRecorded entry, CancellationToken cancellationToken);
}
