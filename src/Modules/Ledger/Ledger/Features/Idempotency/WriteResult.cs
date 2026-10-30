using System.Text.Json;
using Ledger.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Ledger.Features.Idempotency;

// Resposta de um comando idempotente como status e corpo JSON: o que a chave guarda e a repetição devolve (ADR-0014).
internal sealed record WriteResult(int Status, string Body)
{
    internal const string ReplayedHeader = "Idempotent-Replayed";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal bool Replayed { get; init; }

    // Falha transitória: a transação é desfeita e a chave não é consumida.
    internal bool IsTransient { get; init; }

    internal static WriteResult Created(object body) =>
        new(StatusCodes.Status201Created, JsonSerializer.Serialize(body, Json));

    internal static WriteResult Problem(ProblemDetails problem) =>
        new(problem.Status ?? StatusCodes.Status500InternalServerError, JsonSerializer.Serialize(problem, Json));

    // Lock esgotado é a única recusa transitória: repetir pode dar outro resultado.
    internal static WriteResult Rejected(LedgerError error) =>
        Problem(LedgerProblems.Describe(error)) with { IsTransient = error == LedgerError.ConcurrencyTimeout };

    internal IResult ToHttpResult() => new HttpResult(this);

    private sealed class HttpResult(WriteResult result) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            if (result.Replayed)
            {
                httpContext.Response.Headers[ReplayedHeader] = "true";
            }

            if (result.Status >= StatusCodes.Status400BadRequest)
            {
                var problem = JsonSerializer.Deserialize<ProblemDetails>(result.Body, Json)!;
                await LedgerProblems.Result(problem).ExecuteAsync(httpContext);
                return;
            }

            using var body = JsonDocument.Parse(result.Body);
            await Results.Json(body.RootElement, statusCode: result.Status).ExecuteAsync(httpContext);
        }
    }
}
