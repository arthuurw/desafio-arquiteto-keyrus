using System.Diagnostics.CodeAnalysis;
using BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

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
}
