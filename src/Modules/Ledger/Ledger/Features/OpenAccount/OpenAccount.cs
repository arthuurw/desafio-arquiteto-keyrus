using BuildingBlocks.Identity;
using Ledger.Domain;
using Ledger.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ledger.Features.OpenAccount;

internal sealed record OpenAccountCommand(Guid CustomerId) : ICommand<Guid>;

// A conta nasce com saldo, limite e versão zerados (RF-03).
internal sealed class OpenAccountHandler(LedgerDbContext db) : ICommandHandler<OpenAccountCommand, Guid>
{
    public async ValueTask<Guid> Handle(OpenAccountCommand command, CancellationToken cancellationToken)
    {
        var account = Account.Open(command.CustomerId, DateTimeOffset.UtcNow);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return account.Id;
    }
}

internal static class OpenAccountEndpoint
{
    internal static void Map(IEndpointRouteBuilder accounts) =>
        accounts.MapPost("/", async (HttpRequest request, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var input = await LedgerInput.ReadAsync(request, cancellationToken);
                var customerId = input.CustomerId("customerId");
                if (!input.IsValid || customerId is null)
                {
                    return LedgerProblems.Validation(input.Errors);
                }

                var id = await mediator.Send(new OpenAccountCommand(customerId.Value), cancellationToken);
                return Results.Json(new { id }, statusCode: StatusCodes.Status201Created);
            })
            .RequireScope(Scopes.LedgerAccountsManage);
}
