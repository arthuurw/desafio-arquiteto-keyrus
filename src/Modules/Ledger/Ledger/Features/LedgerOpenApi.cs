using System.Globalization;
using System.Text.Json.Nodes;
using BuildingBlocks.Identity;
using Ledger.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;

namespace Ledger.Features;

// Contrato publicado de cada rota (ADR-0020), declarado ao lado dela para mudar junto: escopo, chave de idempotência
// e os `type` de Problem Details de cada status. O consumidor trata o erro pelo `type`, então ele vai como enum.
internal static class LedgerOpenApi
{
    internal const string SecurityScheme = "Bearer";

    private const string ProblemJson = "application/problem+json";

    private static readonly Dictionary<int, string> StatusDescriptions = new()
    {
        [StatusCodes.Status400BadRequest] = "Requisição inválida.",
        [StatusCodes.Status401Unauthorized] = "Token ausente ou inválido.",
        [StatusCodes.Status403Forbidden] = "O token não concede o escopo da rota.",
        [StatusCodes.Status404NotFound] = "Recurso não encontrado, ou de outro titular.",
        [StatusCodes.Status409Conflict] = "Conflito com o estado atual da conta.",
        [StatusCodes.Status422UnprocessableEntity] = "Recusa de negócio.",
        [StatusCodes.Status500InternalServerError] = "Erro interno; o detalhe fica só no log, ligado pelo `traceId`.",
    };

    // Toda rota protegida pode responder estes, além dos que ela declara.
    private static readonly Dictionary<int, string[]> CommonProblems = new()
    {
        [StatusCodes.Status401Unauthorized] = ["authentication-required"],
        [StatusCodes.Status403Forbidden] = ["insufficient-scope"],
        [StatusCodes.Status500InternalServerError] = ["internal-error"],
    };

    // A rota declara só as recusas próprias; o status e o `type` de cada uma vêm do catálogo de LedgerProblems.
    internal static RouteHandlerBuilder Documented(
        this RouteHandlerBuilder route,
        string summary,
        string description,
        bool idempotencyKey,
        params LedgerError[] errors)
    {
        var problems = RouteProblems(idempotencyKey, errors);
        return route
            .WithSummary(summary)
            .WithDescription(description)
            .AddOpenApiOperationTransformer((operation, context, _) =>
            {
                // O escopo vem da política da rota: o documento não diverge do que a autorização exige.
                var scopes = context.Description.ActionDescriptor.EndpointMetadata
                    .OfType<AuthorizationPolicy>()
                    .SelectMany(policy => policy.Requirements.OfType<ScopeRequirement>())
                    .Select(requirement => requirement.Scope)
                    .ToList();
                operation.Security =
                [
                    new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(SecurityScheme, context.Document)] = scopes },
                ];

                if (idempotencyKey)
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = IdempotencyKeyHeader.Name,
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "UUID gerado pelo client para cada operação, no formato 00000000-0000-0000-0000-000000000000. "
                            + "A repetição com a mesma chave e o mesmo conteúdo devolve a resposta original com `Idempotent-Replayed: true`.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
                    });
                }

                operation.Responses ??= [];
                foreach (var (status, types) in problems.Concat(CommonProblems).OrderBy(problem => problem.Key))
                {
                    operation.Responses[status.ToString(CultureInfo.InvariantCulture)] = Problem(status, types);
                }

                return Task.CompletedTask;
            });
    }

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
            problems[StatusCodes.Status422UnprocessableEntity].Add(LedgerProblems.IdempotencyKeyReused().Type!);
        }

        return problems.ToDictionary(problem => problem.Key, problem => problem.Value.ToArray());
    }

    private static OpenApiResponse Problem(int status, string[] types)
    {
        var properties = new Dictionary<string, IOpenApiSchema>
        {
            ["type"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [.. types.Select(type => JsonValue.Create(type))] },
            ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Enum = [JsonValue.Create(status)] },
            ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String },
        };
        if (types.Contains("validation-error"))
        {
            properties["errors"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "Campo do corpo -> mensagens de erro dele.",
                AdditionalProperties = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Items = new OpenApiSchema { Type = JsonSchemaType.String },
                },
            };
        }

        return new OpenApiResponse
        {
            Description = StatusDescriptions[status],
            Content = new Dictionary<string, OpenApiMediaType>
            {
                [ProblemJson] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string> { "type", "title", "status", "traceId" },
                        Properties = properties,
                    },
                },
            },
        };
    }
}
