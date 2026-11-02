using System.Globalization;
using System.Text.Json.Nodes;
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
        // Só com `at` ou `to`: a leitura de um instante passado espera a escrita em voo (ADR-0045).
        [StatusCodes.Status409Conflict] = [ProblemResponses.ConcurrencyTimeout().Type!],
    };

    // A rota lê a query string sem binding, então os parâmetros entram no contrato aqui.
    internal static Action<OpenApiOperation> Query(params QueryParameter[] parameters) =>
        operation =>
        {
            operation.Parameters ??= [];
            foreach (var parameter in parameters)
            {
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = parameter.Key,
                    In = ParameterLocation.Query,
                    Required = false,
                    Description = parameter.Description,
                    Schema = new OpenApiSchema
                    {
                        Type = parameter.Type,
                        Format = parameter.Format,
                        Minimum = parameter.Range?.Minimum.ToString(CultureInfo.InvariantCulture),
                        Maximum = parameter.Range?.Maximum.ToString(CultureInfo.InvariantCulture),
                        Default = parameter.Range is { } range ? JsonValue.Create(range.Default) : null,
                    },
                });
            }
        };

    internal static QueryParameter Instant(string name, string description) =>
        new(name, description + " ISO 8601 com offset explícito (`Z` ou `±hh:mm`).", JsonSchemaType.String, "date-time");
}

// Parâmetro opcional da query string no contrato publicado.
internal sealed record QueryParameter(
    string Key, string Description, JsonSchemaType Type, string? Format = null, IntegerRange? Range = null);

// Faixa e padrão de um parâmetro inteiro, os mesmos que o endpoint aplica.
internal sealed record IntegerRange(int Minimum, int Maximum, int Default);
