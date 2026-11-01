using System.Text.Json;
using BuildingBlocks.Identity;

namespace BuildingBlocks.Idempotency;

// Os bytes sobre os quais cada módulo calcula o hash da requisição (ADR-0037).
public static class RequestFields
{
    public static byte[] Serialize(IIdempotentRequest request)
    {
        // O titular entra no hash: outro cliente do mesmo client não reproduz a resposta deste.
        string[] fields =
        [
            .. request.RequestFields,
            request.Caller.Mode == CallerMode.Delegated ? "delegated" : "service",
            request.Caller.CustomerId?.ToString("D") ?? string.Empty,
        ];

        // Array JSON e não concatenação: nenhum valor consegue se passar por dois campos.
        return JsonSerializer.SerializeToUtf8Bytes(fields);
    }
}
