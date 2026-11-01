using BuildingBlocks.Http;
using BuildingBlocks.Identity;
using Ledger.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;

namespace Positions.Features.GetStatement;

// Extrato do mais recente ao mais antigo, paginado por cursor em `sequence` (ADR-0039).
internal static class GetStatementEndpoint
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 200;

    internal static void Map(IEndpointRouteBuilder accounts) =>
        accounts.MapGet("/{id}/statement", HandleAsync)
            .RequireScope(Scopes.PositionsRead)
            .Produces<Statement>(StatusCodes.Status200OK)
            .Documented(
                "Consulta o extrato",
                "Lançamentos com `from <= recordedAt < to` (ISO 8601 com offset, ambos opcionais), em `sequence` decrescente. "
                    + "`limit` de 1 a 200, padrão 50. A página seguinte vem pelo `cursor` com o `nextCursor` recebido, "
                    + "reenviando `from`, `to` e `limit`; lançamentos novos não entram nas páginas seguintes.",
                PositionsOpenApi.Problems,
                PositionsOpenApi.Query(
                    PositionsOpenApi.Instant("from", "Início do intervalo, inclusivo."),
                    PositionsOpenApi.Instant("to", "Fim do intervalo, exclusivo."),
                    new QueryParameter("limit", "Itens por página, de 1 a 200; padrão 50.", JsonSchemaType.Integer, "int32"),
                    new QueryParameter("cursor", "O `nextCursor` da página anterior.", JsonSchemaType.String)));

    private static async Task<IResult> HandleAsync(
        string id, HttpRequest request, Caller caller, FreshRead read, CancellationToken cancellationToken)
    {
        var query = new PositionsQuery(request.Query);
        var from = query.Instant("from");
        var to = query.Instant("to");
        var limit = query.Limit("limit", DefaultLimit, MaxLimit);
        var cursor = query.Cursor("cursor");
        if (from > to)
        {
            query.Fail("from", "Informe `from` anterior ou igual a `to`.");
        }

        if (!query.IsValid)
        {
            return ProblemResponses.Validation(query.Errors);
        }

        var snapshot = await read.SnapshotAsync(id, caller, cancellationToken);
        if (snapshot is null)
        {
            return ProblemResponses.Result(ProblemResponses.AccountNotFound());
        }

        // Um item a mais diz se há página seguinte sem uma segunda consulta.
        var entries = await read.EntryPageAsync(
            new EntryPageQuery(
                snapshot.AccountId,
                snapshot.Version,
                cursor?.BeforeSequence,
                from is { } start ? Microsecond.Ceiling(start) : null,
                to is { } end ? Microsecond.Ceiling(end) : null,
                limit + 1),
            cancellationToken);
        var items = entries.Take(limit).Select(StatementItem.From).ToList();
        var nextCursor = entries.Count > limit ? new StatementCursor(items[^1].Sequence).Encode() : null;
        return Results.Ok(new Statement(snapshot.AccountId, items, nextCursor));
    }
}
