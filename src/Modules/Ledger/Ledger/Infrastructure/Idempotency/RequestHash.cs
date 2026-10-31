using System.Security.Cryptography;
using System.Text.Json;
using BuildingBlocks.Identity;

namespace Ledger.Infrastructure.Idempotency;

// SHA-256 do comando interpretado: "10.0" e "10.00" são a mesma requisição, outro valor não.
internal static class RequestHash
{
    internal static byte[] Of(IIdempotentCommand command)
    {
        // O titular entra no hash: outro cliente do mesmo client não reproduz a resposta deste.
        string[] fields =
        [
            .. command.RequestFields,
            command.Caller.Mode == CallerMode.Delegated ? "delegated" : "service",
            command.Caller.CustomerId?.ToString("D") ?? string.Empty,
        ];

        // Array JSON e não concatenação: nenhum valor consegue se passar por dois campos.
        return SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields));
    }
}
