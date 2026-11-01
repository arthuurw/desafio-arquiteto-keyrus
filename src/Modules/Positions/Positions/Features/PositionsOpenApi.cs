using BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;

namespace Positions.Features;

internal static class PositionsOpenApi
{
    // As recusas próprias das consultas; 401, 403 e 500 vêm do contrato comum.
    internal static readonly IReadOnlyDictionary<int, string[]> Problems = new Dictionary<int, string[]>
    {
        [StatusCodes.Status400BadRequest] = [ProblemResponses.ValidationErrorType],
        [StatusCodes.Status404NotFound] = [ProblemResponses.AccountNotFound().Type!],
    };

    // A rota lê a query string sem binding, então os parâmetros entram no contrato aqui.
    internal static Action<OpenApiOperation> Query(params QueryParameter[] parameters) =>
        operation =>
        {
            operation.Parameters ??= [];
            foreach (var (name, description, type, format) in parameters)
            {
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = name,
                    In = ParameterLocation.Query,
                    Required = false,
                    Description = description,
                    Schema = new OpenApiSchema { Type = type, Format = format },
                });
            }
        };

    internal static QueryParameter Instant(string name, string description) =>
        new(name, description + " ISO 8601 com offset explícito (`Z` ou `±hh:mm`).", JsonSchemaType.String, "date-time");
}

// Parâmetro opcional da query string no contrato publicado.
internal sealed record QueryParameter(string Key, string Description, JsonSchemaType Type, string? Format = null);
