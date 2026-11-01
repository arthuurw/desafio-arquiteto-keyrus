using System.Security.Cryptography;
using System.Text.Json;

namespace Customers.Infrastructure.Vault;

// DEK de cada cliente, guardada fora do banco (ADR-0016): o backup do PostgreSQL tem só ciphertext.
internal interface IKeyStore
{
    // Grava a DEK de um cliente novo; nunca sobrescreve uma existente.
    Task CreateAsync(Guid customerId, byte[] dek, CancellationToken cancellationToken);

    // A DEK do cliente, numa cópia que o chamador zera depois de usar.
    Task<byte[]> ReadAsync(Guid customerId, CancellationToken cancellationToken);
}

// KV v2 em `customers/data/dek/{customerId}`, campo `key` em base64, escrito com `cas=0` (ADR-0016).
internal sealed class OpenBaoKeyStore(OpenBaoClient vault) : IKeyStore
{
    internal static string PathOf(Guid customerId) => $"customers/data/dek/{customerId:D}";

    public async Task CreateAsync(Guid customerId, byte[] dek, CancellationToken cancellationToken)
    {
        // `cas=0` só grava se o caminho ainda não existe; a política, sem `update`, recusa a sobrescrita também.
        var body = new { options = new { cas = 0 }, data = new { key = Convert.ToBase64String(dek) } };
        _ = await vault.SendAsync(HttpMethod.Post, PathOf(customerId), body, cancellationToken)
            ?? throw new InvalidOperationException($"OpenBao has no KV engine for customer {customerId:D}.");
    }

    public async Task<byte[]> ReadAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var secret = await vault.SendAsync(HttpMethod.Get, PathOf(customerId), null, cancellationToken)
            ?? throw new InvalidOperationException($"OpenBao has no DEK for customer {customerId:D}.");
        var dek = Convert.FromBase64String(secret.GetProperty("data").GetProperty("data").GetProperty("key").GetString()
            ?? throw new JsonException($"DEK of customer {customerId:D} has no key."));
        if (dek.Length != Crypto.FieldCipher.KeySize)
        {
            CryptographicOperations.ZeroMemory(dek);
            throw new InvalidOperationException($"DEK of customer {customerId:D} is not {Crypto.FieldCipher.KeySize} bytes.");
        }

        return dek;
    }
}
