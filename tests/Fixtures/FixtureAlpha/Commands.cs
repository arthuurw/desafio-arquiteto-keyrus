using Mediator;

namespace FixtureAlpha.Features;

// Fixture da regra de comandos idempotentes: um protegido, um isento e uma violação.
internal interface IFixtureIdempotentCommand : ICommand<int>;

internal sealed record ProtectedCommand : IFixtureIdempotentCommand;

internal sealed record ExemptCommand : ICommand<int>;

// Violação: comando de escrita sem a interface idempotente.
internal sealed record UnprotectedCommand : ICommand<int>;
