using BuildingBlocks.Http;
using System.ComponentModel;
using BuildingBlocks.Identity;
using BuildingBlocks.Idempotency;
using Ledger.Domain;
using Ledger.Infrastructure.Idempotency;
using Ledger.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ledger.Features.OpenAccount;

internal sealed record OpenAccountCommand(Guid CustomerId, Caller Caller, Guid IdempotencyKey) : IIdempotentCommand
{
    public IReadOnlyList<string> RequestFields => [IdempotentOperation.OpenAccount, CustomerId.ToString("D")];
}

// Limite de crédito padrão zero (RF-03). O delegado só abre conta para o titular do token (ADR-0013); a recusa
// é guardada com a chave, como as demais.
internal sealed class OpenAccountHandler(LedgerDbContext db) : ICommandHandler<OpenAccountCommand, WriteResult>
{
    public async ValueTask<WriteResult> Handle(OpenAccountCommand command, CancellationToken cancellationToken)
    {
        if (!command.Caller.CanAccessCustomer(command.CustomerId))
        {
            return LedgerProblems.Refused(LedgerError.CustomerNotFound);
        }

        var account = Account.Open(command.CustomerId);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return WriteResult.Created(new AccountCreated(account.Id));
    }
}

// Corpo da abertura, para o contrato publicado; a leitura é do LedgerInput, que junta todos os erros.
internal sealed record OpenAccountRequest([property: Description("Cliente titular da conta.")] Guid CustomerId);

internal sealed record AccountCreated([property: Description("Id da conta aberta.")] Guid Id);

internal static class OpenAccountEndpoint
{
    internal static void Map(IEndpointRouteBuilder accounts) =>
        accounts.MapPost("/", async (HttpRequest request, Caller caller, ILedgerCommands commands, CancellationToken cancellationToken) =>
            {
                if (!IdempotencyKeyHeader.TryRead(request, out var idempotencyKey, out var keyProblem))
                {
                    return keyProblem;
                }

                var input = await LedgerInput.ReadAsync(request, cancellationToken);
                var customerId = input.CustomerId("customerId");
                if (!input.IsValid || customerId is null)
                {
                    return ProblemResponses.Validation(input.Errors);
                }

                var result = await commands.Send(new OpenAccountCommand(customerId.Value, caller, idempotencyKey), cancellationToken);
                return result.ToHttpResult();
            })
            .RequireScope(Scopes.LedgerAccountsManage)
            .Accepts<OpenAccountRequest>("application/json")
            .Produces<AccountCreated>(StatusCodes.Status201Created)
            .Documented(
                "Abre uma conta",
                "Abre uma conta para o cliente, com saldo e limite de crédito zero. "
                    + "No modo delegado, só para o cliente em nome de quem o token age.",
                idempotencyKey: true,
                LedgerError.CustomerNotFound);
}
