using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Ledger.Features;

// Lido antes do corpo e do id da rota: a chave é pré-condição de todo `POST` do Ledger (ADR-0014).
internal static class IdempotencyKeyHeader
{
    internal const string Name = "Idempotency-Key";

    internal static bool TryRead(HttpRequest request, out Guid key, [NotNullWhen(false)] out IResult? problem)
    {
        key = Guid.Empty;
        var values = request.Headers[Name];
        if (StringValues.IsNullOrEmpty(values))
        {
            problem = LedgerProblems.IdempotencyKeyMissing();
            return false;
        }

        // Só a forma textual padrão do UUID, com hífens; dois valores no header também são recusados.
        if (values.Count != 1 || !Guid.TryParseExact(values[0], "D", out key))
        {
            problem = LedgerProblems.IdempotencyKeyInvalid();
            return false;
        }

        problem = null;
        return true;
    }
}
