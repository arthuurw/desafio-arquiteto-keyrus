using BuildingBlocks.Identity;
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

// Limite de crédito padrão zero (RF-03).
internal sealed class OpenAccountHandler(LedgerDbContext db) : ICommandHandler<OpenAccountCommand, WriteResult>
{
    public async ValueTask<WriteResult> Handle(OpenAccountCommand command, CancellationToken cancellationToken)
    {
        var account = Account.Open(command.CustomerId, DateTimeOffset.UtcNow);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return WriteResult.Created(new { id = account.Id });
    }
}

internal static class OpenAccountEndpoint
{
    internal static void Map(IEndpointRouteBuilder accounts) =>
        accounts.MapPost("/", async (HttpRequest request, Caller caller, IMediator mediator, CancellationToken cancellationToken) =>
            {
                if (!IdempotencyKeyHeader.TryRead(request, out var idempotencyKey, out var keyProblem))
                {
                    return keyProblem;
                }

                var input = await LedgerInput.ReadAsync(request, cancellationToken);
                var customerId = input.CustomerId("customerId");
                if (!input.IsValid || customerId is null)
                {
                    return LedgerProblems.Validation(input.Errors);
                }

                var result = await mediator.Send(new OpenAccountCommand(customerId.Value, caller, idempotencyKey), cancellationToken);
                return result.ToHttpResult();
            })
            .RequireScope(Scopes.LedgerAccountsManage);
}
