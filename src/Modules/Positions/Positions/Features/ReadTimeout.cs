namespace Positions.Features;

// Teto de cada leitura no MongoDB (ADR-0021).
internal sealed record ReadTimeout(TimeSpan Value);
