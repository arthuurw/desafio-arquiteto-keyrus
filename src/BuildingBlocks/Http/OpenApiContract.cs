using System.Globalization;
using System.Text.Json.Nodes;
using BuildingBlocks.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;

namespace BuildingBlocks.Http;

// Contrato publicado de cada rota (ADR-0042), declarado ao lado dela para mudar junto: escopo e os `type` de Problem
// Details de cada status. O consumidor trata o erro pelo `type`, então ele vai como enum.
public static class OpenApiContract
{
    public const string SecurityScheme = "Bearer";

    private const string ProblemJson = "application/problem+json";

    private static readonly Dictionary<int, string> StatusDescriptions = new()
    {
        [StatusCodes.Status400BadRequest] = "Requisição inválida.",
        [StatusCodes.Status401Unauthorized] = "Token ausente ou inválido.",
        [StatusCodes.Status403Forbidden] = "O token não concede o escopo da rota.",
        [StatusCodes.Status404NotFound] = "Recurso não encontrado, ou de outro titular.",
        [StatusCodes.Status409Conflict] = "Conflito com o estado atual do recurso.",
        [StatusCodes.Status422UnprocessableEntity] = "Recusa de negócio.",
        [StatusCodes.Status500InternalServerError] = "Erro interno; o detalhe fica só no log, ligado pelo `traceId`.",
        [StatusCodes.Status503ServiceUnavailable] = "Dependência indisponível; repita depois do `Retry-After`.",
    };

    // Toda rota protegida pode responder estes, além dos que ela declara.
    private static readonly Dictionary<int, string[]> CommonProblems = new()
    {
        [StatusCodes.Status401Unauthorized] = ["authentication-required"],
        [StatusCodes.Status403Forbidden] = ["insufficient-scope"],
        [StatusCodes.Status500InternalServerError] = ["internal-error"],
    };

    // `problems`: status -> os `type` próprios da rota. `configure` acrescenta o que é só da rota, como um header.
    public static RouteHandlerBuilder Documented(
        this RouteHandlerBuilder route,
        string summary,
        string description,
        IReadOnlyDictionary<int, string[]> problems,
        Action<OpenApiOperation>? configure = null) =>
        route
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

                configure?.Invoke(operation);

                operation.Responses ??= [];
                foreach (var (status, types) in problems.Concat(CommonProblems).OrderBy(problem => problem.Key))
                {
                    operation.Responses[status.ToString(CultureInfo.InvariantCulture)] = Problem(status, types);
                }

                return Task.CompletedTask;
            });

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
        if (types.Contains(ProblemResponses.ValidationErrorType))
        {
            properties["errors"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "Campo da requisição -> mensagens de erro dele.",
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
