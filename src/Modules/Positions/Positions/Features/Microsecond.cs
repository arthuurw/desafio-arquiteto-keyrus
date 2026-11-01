namespace Positions.Features;

// `recordedAt` tem precisão de microssegundo, a do PostgreSQL; um instante pedido pode ter décimos de microssegundo.
// Cada comparação arredonda para o lado que mantém o resultado exato: `recordedAt <= at` vale para o microssegundo de
// baixo de `at`, e `recordedAt >= from` e `recordedAt < to`, para o de cima de `from` e `to`.
internal static class Microsecond
{
    internal static DateTimeOffset Floor(DateTimeOffset instant) =>
        instant.AddTicks(-(instant.UtcTicks % TimeSpan.TicksPerMicrosecond));

    internal static DateTimeOffset Ceiling(DateTimeOffset instant) =>
        instant.UtcTicks % TimeSpan.TicksPerMicrosecond == 0 ? instant : Floor(instant).AddTicks(TimeSpan.TicksPerMicrosecond);
}
