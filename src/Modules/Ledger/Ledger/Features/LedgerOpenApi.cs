using BuildingBlocks.Http;
using BuildingBlocks.Idempotency;
using Ledger.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Ledger.Features;

// Contrato publicado das rotas do Ledger (ADR-0039): as recusas de cada uma e a chave de idempotência das escritas.
internal static class LedgerOpenApi
{
    // A rota declara só as recusas próprias; o status e o `type` de cada uma vêm do catálogo de LedgerProblems.
    internal static RouteHandlerBuilder Documented(
        this RouteHandlerBuilder route,
        string summary,
        string description,
        bool idempotencyKey,
        params LedgerError[] errors) =>
        route.Documented(
            summary,
            description,
            RouteProblems(idempotencyKey, errors),
            operation =>
            {
                if (!idempotencyKey)
                {
                    return;
                }

                IdempotencyKeyHeader.Document(operation);
            });

    // Toda rota valida o corpo e pode esgotar a espera por lock; a rota com chave também recusa a chave.
    private static Dictionary<int, string[]> RouteProblems(bool idempotencyKey, LedgerError[] errors)
    {
        var problems = new Dictionary<int, List<string>>
        {
            [StatusCodes.Status400BadRequest] = ["validation-error"],
            [StatusCodes.Status409Conflict] = [LedgerProblems.Describe(LedgerError.ConcurrencyTimeout).Type!],
        };
        if (idempotencyKey)
        {
            problems[StatusCodes.Status400BadRequest].AddRange(["idempotency-key-missing", "idempotency-key-invalid"]);
        }

        foreach (var problem in errors.Select(LedgerProblems.Describe))
        {
            var status = problem.Status!.Value;
            problems.TryAdd(status, []);
            problems[status].Add(problem.Type!);
        }

        if (idempotencyKey)
        {
            problems.TryAdd(StatusCodes.Status422UnprocessableEntity, []);
            problems[StatusCodes.Status422UnprocessableEntity].Add(IdempotencyProblems.KeyReused().Type!);
        }

        return problems.ToDictionary(problem => problem.Key, problem => problem.Value.ToArray());
    }
}
