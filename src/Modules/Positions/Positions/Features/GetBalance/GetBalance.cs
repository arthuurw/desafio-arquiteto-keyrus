using BuildingBlocks.Http;
using BuildingBlocks.Identity;
using Ledger.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Positions.Features.GetBalance;

// Saldo atual, ou o saldo em um momento: o `balanceAfter` do último lançamento até `at` (ADR-0002, ADR-0003).
internal static class GetBalanceEndpoint
{
    internal static void Map(IEndpointRouteBuilder accounts) =>
        accounts.MapGet("/{id}/balance", HandleAsync)
            .RequireScope(Scopes.PositionsRead)
            .Produces<BalanceResponse>(StatusCodes.Status200OK)
            .Documented(
                "Consulta o saldo",
                "Sem `at`, o saldo atual, com toda operação confirmada antes da consulta. Com `at` (ISO 8601 com offset), "
                    + "o saldo depois do último lançamento registrado até aquele instante, inclusive; `at` no futuro é recusado.",
                PositionsOpenApi.Problems,
                PositionsOpenApi.Query(PositionsOpenApi.Instant("at", "Instante da consulta; ausente, o saldo atual.")));

    private static async Task<IResult> HandleAsync(
        string id, HttpRequest request, Caller caller, ILedgerQueries ledger, FreshRead read, CancellationToken cancellationToken)
    {
        var query = new PositionsQuery(request.Query);
        var at = query.Instant("at");
        if (!query.IsValid)
        {
            return ProblemResponses.Validation(query.Errors);
        }

        // Id fora do formato não existe; conta de outro titular responde igual (ADR-0013).
        var snapshot = Guid.TryParse(id, out var accountId)
            ? await ledger.AccountSnapshotAsync(accountId, caller, cancellationToken)
            : null;
        if (snapshot is null)
        {
            return ProblemResponses.Result(ProblemResponses.AccountNotFound());
        }

        if (at is null)
        {
            var balance = await read.CurrentBalanceAsync(snapshot, cancellationToken);
            return Results.Ok(new BalanceResponse(
                snapshot.AccountId, PositionsFormat.Money(balance), PositionsFormat.Utc(snapshot.Now), snapshot.Version));
        }

        // O futuro ainda pode receber lançamentos: a mesma consulta daria respostas diferentes.
        if (at > snapshot.Now)
        {
            query.Fail("at", "Informe um instante que não esteja no futuro.");
            return ProblemResponses.Validation(query.Errors);
        }

        var entry = await read.LastEntryAtAsync(snapshot, at.Value, cancellationToken);
        return Results.Ok(new BalanceResponse(
            snapshot.AccountId, PositionsFormat.Money(entry?.BalanceAfter ?? 0m), PositionsFormat.Utc(at.Value), entry?.Sequence ?? 0));
    }
}
