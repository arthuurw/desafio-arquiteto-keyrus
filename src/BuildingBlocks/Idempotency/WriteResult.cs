using System.Text.Json;
using BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BuildingBlocks.Idempotency;

// Resposta de um comando idempotente como status e corpo JSON: o que a chave guarda e a repetição devolve (ADR-0037).
public sealed record WriteResult(int Status, string Body)
{
    public const string ReplayedHeader = "Idempotent-Replayed";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Replayed { get; init; }

    // Falha que repetir pode mudar: o behavior desfaz a transação, e a chave não é consumida.
    public bool Transient { get; init; }

    // Cabeçalhos da resposta HTTP que não são guardados com a chave, como o `Retry-After` de uma falha transitória.
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public static WriteResult Created(object body) =>
        new(StatusCodes.Status201Created, JsonSerializer.Serialize(body, Json));

    public static WriteResult Problem(ProblemDetails problem) =>
        new(problem.Status ?? StatusCodes.Status500InternalServerError, JsonSerializer.Serialize(problem, Json));

    public IResult ToHttpResult() => new HttpResult(this);

    private sealed class HttpResult(WriteResult result) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            if (result.Replayed)
            {
                httpContext.Response.Headers[ReplayedHeader] = "true";
            }

            foreach (var (name, value) in result.Headers)
            {
                httpContext.Response.Headers[name] = value;
            }

            if (result.Status >= StatusCodes.Status400BadRequest)
            {
                var problem = JsonSerializer.Deserialize<ProblemDetails>(result.Body, Json)!;
                await ProblemResponses.Result(problem).ExecuteAsync(httpContext);
                return;
            }

            using var body = JsonDocument.Parse(result.Body);
            await Results.Json(body.RootElement, statusCode: result.Status).ExecuteAsync(httpContext);
        }
    }
}
