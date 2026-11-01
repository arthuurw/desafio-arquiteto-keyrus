using BuildingBlocks.Idempotency;
using Mediator;

namespace Customers.Features;

// Única porta de despacho dos endpoints de Customers: envia pelo mediador gerado deste módulo, nunca pelo `IMediator`,
// que com dois módulos resolve o do outro (ADR-0036).
internal interface ICustomersCommands
{
    ValueTask<WriteResult> Send(ICommand<WriteResult> command, CancellationToken cancellationToken);
}

internal sealed class CustomersCommands(Mediation.Mediator mediator) : ICustomersCommands
{
    public ValueTask<WriteResult> Send(ICommand<WriteResult> command, CancellationToken cancellationToken) =>
        mediator.Send(command, cancellationToken);
}
