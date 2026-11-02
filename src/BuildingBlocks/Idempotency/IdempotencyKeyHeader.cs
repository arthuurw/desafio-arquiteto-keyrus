using System.Diagnostics.CodeAnalysis;
using BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi;

namespace BuildingBlocks.Idempotency;

// Lido antes do corpo e do id da rota: a chave é pré-condição de todo `POST` (ADR-0037).
public static class IdempotencyKeyHeader
{
    public const string Name = "Idempotency-Key";

    public static bool TryRead(HttpRequest request, out Guid key, [NotNullWhen(false)] out IResult? problem)
    {
        key = Guid.Empty;
        var values = request.Headers[Name];
        if (StringValues.IsNullOrEmpty(values))
        {
            problem = ProblemResponses.Result(IdempotencyProblems.KeyMissing());
            return false;
        }

        // Só a forma textual padrão do UUID, com hífens; dois valores no header também são recusados.
        if (values.Count != 1 || !Guid.TryParseExact(values[0], "D", out key))
        {
            problem = ProblemResponses.Result(IdempotencyProblems.KeyInvalid());
            return false;
        }

        problem = null;
        return true;
    }

    // As recusas da chave no contrato da rota com chave: falta e formato no `400`, reuso no `422`.
    public static void AddProblems(IDictionary<int, List<string>> problems)
    {
        Add(problems, StatusCodes.Status400BadRequest, IdempotencyProblems.KeyMissing().Type!);
        Add(problems, StatusCodes.Status400BadRequest, IdempotencyProblems.KeyInvalid().Type!);
        Add(problems, StatusCodes.Status422UnprocessableEntity, IdempotencyProblems.KeyReused().Type!);
    }

    // O header no contrato publicado da rota (ADR-0042).
    public static void Document(OpenApiOperation operation)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = Name,
            In = ParameterLocation.Header,
            Required = true,
            Description = "UUID gerado pelo client para cada operação, no formato 00000000-0000-0000-0000-000000000000. "
                + "A repetição com a mesma chave e o mesmo conteúdo devolve a resposta original com `Idempotent-Replayed: true`.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
        });
    }

    private static void Add(IDictionary<int, List<string>> problems, int status, string type)
    {
        if (!problems.TryGetValue(status, out var types))
        {
            problems[status] = types = [];
        }

        types.Add(type);
    }
}
