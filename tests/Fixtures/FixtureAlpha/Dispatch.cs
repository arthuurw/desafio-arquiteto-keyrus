using Mediator;

namespace FixtureAlpha.Features;

// Fixture da regra de despacho: um endpoint que envia pelo `IMediator` em vez da facade do módulo.
internal static class MediatorEndpoint
{
    internal static ValueTask<int> Handle(IMediator mediator, CancellationToken cancellationToken) =>
        mediator.Send(new ProtectedCommand(), cancellationToken);
}
