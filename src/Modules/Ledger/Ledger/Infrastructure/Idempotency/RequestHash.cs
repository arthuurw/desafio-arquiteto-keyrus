using System.Security.Cryptography;
using BuildingBlocks.Idempotency;

namespace Ledger.Infrastructure.Idempotency;

// SHA-256 do comando interpretado: "10.0" e "10.00" são a mesma requisição, outro valor não. Sem chave: os valores do
// Ledger são financeiros, não dado pessoal (ADR-0037).
internal static class RequestHash
{
    internal static byte[] Of(IIdempotentCommand command) => SHA256.HashData(RequestFields.Serialize(command));
}
