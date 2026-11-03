namespace Positions.Features;

// `recordedAt` tem precisão de microssegundo, a do PostgreSQL; um instante pedido pode ter décimos de microssegundo.
// Cada comparação arredonda para o lado que mantém o resultado exato: `recordedAt <= at` vale para o microssegundo de
// baixo de `at`, e `recordedAt >= from` e `recordedAt < to`, para o de cima de `from` e `to`.
internal static class Microsecond
{
    internal static DateTimeOffset Floor(DateTimeOffset instant) =>
        instant.AddTicks(-(instant.UtcTicks % TimeSpan.TicksPerMicrosecond));

    // Perto de `DateTimeOffset.MaxValue` o microssegundo de cima não existe: fica o maior instante, que nenhum
    // `recordedAt` alcança, e a página sai vazia em vez de estourar.
    internal static DateTimeOffset Ceiling(DateTimeOffset instant)
    {
        if (instant.UtcTicks % TimeSpan.TicksPerMicrosecond == 0)
        {
            return instant;
        }

        var floor = Floor(instant);
        return DateTimeOffset.MaxValue.UtcTicks - floor.UtcTicks < TimeSpan.TicksPerMicrosecond
            ? DateTimeOffset.MaxValue
            : floor.AddTicks(TimeSpan.TicksPerMicrosecond);
    }
}
