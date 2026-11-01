using System.Globalization;

namespace Positions.Features;

// Dinheiro como string com 2 casas (ADR-0018); instante em UTC terminado em `Z`.
internal static class PositionsFormat
{
    internal static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    internal static DateTime Utc(DateTimeOffset instant) => instant.UtcDateTime;
}
