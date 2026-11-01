using BuildingBlocks.Http;
using BuildingBlocks.Idempotency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Customers.Features;

// Contrato publicado das rotas de Customers (ADR-0039): toda rota depende do cofre, e a escrita, da chave.
internal static class CustomersOpenApi
{
    internal static RouteHandlerBuilder Documented(
        this RouteHandlerBuilder route,
        string summary,
        string description,
        bool idempotencyKey,
        IReadOnlyDictionary<int, string[]> problems)
    {
        var all = problems.ToDictionary(problem => problem.Key, problem => problem.Value.ToList());
        all[StatusCodes.Status503ServiceUnavailable] = [CustomersProblems.PersonalDataUnavailable().Type!];
        if (idempotencyKey)
        {
            all.TryAdd(StatusCodes.Status400BadRequest, []);
            all[StatusCodes.Status400BadRequest].AddRange([IdempotencyProblems.KeyMissing().Type!, IdempotencyProblems.KeyInvalid().Type!]);
            all[StatusCodes.Status422UnprocessableEntity] = [IdempotencyProblems.KeyReused().Type!];
        }

        return route.Documented(
            summary,
            description,
            all.ToDictionary(problem => problem.Key, problem => problem.Value.ToArray()),
            operation =>
            {
                if (!idempotencyKey)
                {
                    return;
                }

                IdempotencyKeyHeader.Document(operation);
            });
    }
}
