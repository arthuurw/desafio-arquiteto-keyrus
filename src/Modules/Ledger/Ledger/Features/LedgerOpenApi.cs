using System.Globalization;
using System.Text.Json.Nodes;
using BuildingBlocks.Http;
using BuildingBlocks.Idempotency;
using Ledger.Domain;
using Ledger.Features.ChangeCreditLimit;
using Ledger.Features.RecordEntry;
using Ledger.Features.ReverseEntry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Ledger.Features;

// Contrato publicado das rotas do Ledger (ADR-0042): as recusas, a chave de idempotência e o formato dos corpos.
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
                if (idempotencyKey)
                {
                    IdempotencyKeyHeader.Document(operation);
                }

                if (errors.Contains(LedgerError.DuplicateExternalReference))
                {
                    DocumentDuplicateIds(operation);
                }
            });

    // O documento da Api que recebe os schemas dos corpos (o nome padrão do `AddOpenApi`).
    internal const string DocumentName = "v1";

    // O formato e o catálogo que o endpoint aplica ao ler o corpo, no schema que o contrato publica.
    internal static Task DescribeBodies(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(CreditRequest) || type == typeof(DebitRequest))
        {
            SetPattern(schema, "amount", LedgerInput.MoneyContractPattern);
            SetPattern(schema, "externalReference", LedgerInput.ExternalReferenceContractPattern);
            var categories = type == typeof(CreditRequest) ? Categories.Credit : Categories.Debit;
            ((OpenApiSchema)schema.Properties!["category"]).Enum = [.. categories.Select(category => (JsonNode)JsonValue.Create(category))];
        }
        else if (type == typeof(CreditLimitRequest))
        {
            SetPattern(schema, "creditLimit", LedgerInput.MoneyContractPattern);
        }
        else if (type == typeof(ReversalRequest))
        {
            SetPattern(schema, "externalReference", LedgerInput.ExternalReferenceContractPattern);
        }

        return Task.CompletedTask;
    }

    private static void SetPattern(OpenApiSchema schema, string property, string pattern) =>
        ((OpenApiSchema)schema.Properties![property]).Pattern = pattern;

    // Quem vê a conta do lançamento existente recebe os ids dele; o delegado de outro titular, não (ADR-0013).
    private static void DocumentDuplicateIds(OpenApiOperation operation)
    {
        var schema = (OpenApiSchema)operation.Responses![StatusCodes.Status409Conflict.ToString(CultureInfo.InvariantCulture)]
            .Content!["application/problem+json"].Schema!;
        schema.Properties!["entryId"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Format = "uuid",
            Description = "Com `duplicate-external-reference`: o lançamento que já usa a referência, se o chamador vê a conta dele.",
        };
        schema.Properties["accountId"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Format = "uuid",
            Description = "Com `duplicate-external-reference`: a conta desse lançamento, se o chamador a vê.",
        };
    }

    // Toda rota valida o corpo e pode esgotar a espera por lock ou o prazo do comando; a rota com chave também recusa a
    // chave.
    private static Dictionary<int, string[]> RouteProblems(bool idempotencyKey, LedgerError[] errors)
    {
        var problems = new Dictionary<int, List<string>>
        {
            [StatusCodes.Status400BadRequest] = [ProblemResponses.ValidationErrorType],
            [StatusCodes.Status409Conflict] = [LedgerProblems.Describe(LedgerError.ConcurrencyTimeout).Type!],
            [StatusCodes.Status503ServiceUnavailable] = [LedgerProblems.Describe(LedgerError.StatementTimeout).Type!],
        };
        foreach (var problem in errors.Select(LedgerProblems.Describe))
        {
            var status = problem.Status!.Value;
            problems.TryAdd(status, []);
            problems[status].Add(problem.Type!);
        }

        if (idempotencyKey)
        {
            IdempotencyKeyHeader.AddProblems(problems);
        }

        return problems.ToDictionary(problem => problem.Key, problem => problem.Value.ToArray());
    }
}
