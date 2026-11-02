using System.Security.Claims;
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
                    + "o saldo depois do último lançamento registrado até aquele instante, inclusive; `at` no futuro é recusado. "
                    + "Com `at`, a consulta espera a operação em andamento na conta; se ela passa de 2 s, `409 concurrency-timeout`.",
                PositionsOpenApi.Problems,
                PositionsOpenApi.Query(PositionsOpenApi.Instant("at", "Instante da consulta; ausente, o saldo atual.")));

    private static async Task<IResult> HandleAsync(
        string id, HttpRequest request, ClaimsPrincipal user, FreshRead read, CancellationToken cancellationToken)
    {
        var query = new PositionsQuery(request.Query);
        var at = query.Instant("at");
        if (!query.IsValid)
        {
            return ProblemResponses.Validation(query.Errors);
        }

        AccountSnapshot? snapshot;
        try
        {
            snapshot = await read.SnapshotAsync(id, user.ResolvedCaller(), waitForWrites: at is not null, cancellationToken);
        }
        catch (AccountBusyException)
        {
            return ProblemResponses.Result(ProblemResponses.ConcurrencyTimeout());
        }

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

        var balanceAt = await read.BalanceAtAsync(snapshot, Microsecond.Floor(at.Value), cancellationToken);
        return Results.Ok(new BalanceResponse(
            snapshot.AccountId, PositionsFormat.Money(balanceAt.Balance), PositionsFormat.Utc(at.Value), balanceAt.Version));
    }
}
