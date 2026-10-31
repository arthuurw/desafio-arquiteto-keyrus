using BuildingBlocks.Identity;
using Ledger.Domain;
using Ledger.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ledger.Features.ChangeCreditLimit;

internal sealed record ChangeCreditLimitCommand(Guid AccountId, Money CreditLimit, Caller Caller) : ICommand<Outcome<Account>>;

// Trava a conta como um débito: o limite novo é decidido contra o saldo atual, não contra um valor defasado.
internal sealed class ChangeCreditLimitHandler(LedgerDbContext db) : ICommandHandler<ChangeCreditLimitCommand, Outcome<Account>>
{
    // Sem chave de idempotência (só os `POST` a têm): a transação é deste handler, e qualquer recusa a desfaz.
    public async ValueTask<Outcome<Account>> Handle(ChangeCreditLimitCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await db.BeginWriteTransactionAsync(cancellationToken);
        var outcome = await db.WriteAsync(
            command.AccountId,
            command.Caller,
            account => Task.FromResult(account.ChangeCreditLimit(command.CreditLimit) is { } error
                ? Outcome<Account>.Failure(error)
                : Outcome<Account>.Success(account)),
            cancellationToken);
        if (outcome.IsSuccess)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return outcome;
    }
}

internal static class ChangeCreditLimitEndpoint
{
    internal static void Map(IEndpointRouteBuilder accounts) =>
        accounts.MapPut("/{id}/credit-limit", async (string id, HttpRequest request, Caller caller, IMediator mediator, CancellationToken cancellationToken) =>
            {
                if (!Guid.TryParse(id, out var accountId))
                {
                    return LedgerProblems.From(LedgerError.AccountNotFound);
                }

                var input = await LedgerInput.ReadAsync(request, cancellationToken);
                var creditLimit = input.CreditLimit("creditLimit");
                if (!input.IsValid || creditLimit is null)
                {
                    return LedgerProblems.Validation(input.Errors);
                }

                var outcome = await mediator.Send(new ChangeCreditLimitCommand(accountId, creditLimit, caller), cancellationToken);
                return outcome.IsSuccess ? Results.NoContent() : LedgerProblems.From(outcome.Error!.Value);
            })
            .RequireScope(Scopes.LedgerAccountsManage);
}
