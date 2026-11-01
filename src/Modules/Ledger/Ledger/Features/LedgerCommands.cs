using BuildingBlocks.Idempotency;
using Ledger.Domain;
using Mediator;

namespace Ledger.Features;

// Única porta de despacho dos endpoints do Ledger: envia pelo mediador gerado deste módulo, nunca pelo `IMediator`,
// que com dois módulos resolve o do outro (ADR-0036).
internal interface ILedgerCommands
{
    ValueTask<WriteResult> Send(ICommand<WriteResult> command, CancellationToken cancellationToken);

    ValueTask<Outcome<Account>> Send(ICommand<Outcome<Account>> command, CancellationToken cancellationToken);
}

internal sealed class LedgerCommands(Mediation.Mediator mediator) : ILedgerCommands
{
    public ValueTask<WriteResult> Send(ICommand<WriteResult> command, CancellationToken cancellationToken) =>
        mediator.Send(command, cancellationToken);

    public ValueTask<Outcome<Account>> Send(ICommand<Outcome<Account>> command, CancellationToken cancellationToken) =>
        mediator.Send(command, cancellationToken);
}
