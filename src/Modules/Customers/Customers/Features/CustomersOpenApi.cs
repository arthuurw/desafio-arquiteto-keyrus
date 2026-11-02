using BuildingBlocks.Http;
using BuildingBlocks.Idempotency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Customers.Features;

// Contrato publicado das rotas de Customers (ADR-0042): toda rota depende do cofre, e a escrita, da chave.
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
            IdempotencyKeyHeader.AddProblems(all);
        }

        return route.Documented(
            summary,
            description,
            all.ToDictionary(problem => problem.Key, problem => problem.Value.ToArray()),
            idempotencyKey ? IdempotencyKeyHeader.Document : null);
    }
}
