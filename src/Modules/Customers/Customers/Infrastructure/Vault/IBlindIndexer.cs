using System.Globalization;

namespace Customers.Infrastructure.Vault;

// HMAC-SHA256 com a versão da chave que o calculou: após uma rotação, a versão aponta as linhas a recalcular (ADR-0016).
internal sealed record KeyedHash(byte[] Value, int Version);

// HMACs calculados pelo Transit do OpenBao: as chaves nunca saem do cofre, nem para a memória da Api (ADR-0016).
internal interface IBlindIndexer
{
    // Blind index do CPF com os 11 dígitos, chave `customers-blind-index`.
    Task<KeyedHash> CpfAsync(string digits, CancellationToken cancellationToken);

    // Hash da requisição idempotente de Customers, chave `customers-request-hash` (ADR-0037).
    Task<KeyedHash> RequestAsync(byte[] payload, CancellationToken cancellationToken);
}

internal sealed class OpenBaoBlindIndexer(OpenBaoClient vault) : IBlindIndexer
{
    internal const string BlindIndexKey = "customers-blind-index";
    internal const string RequestHashKey = "customers-request-hash";

    private const string Prefix = "vault:v";

    public Task<KeyedHash> CpfAsync(string digits, CancellationToken cancellationToken) =>
        HmacAsync(BlindIndexKey, System.Text.Encoding.UTF8.GetBytes(digits), cancellationToken);

    public Task<KeyedHash> RequestAsync(byte[] payload, CancellationToken cancellationToken) =>
        HmacAsync(RequestHashKey, payload, cancellationToken);

    // SHA2-256 é o algoritmo padrão do endpoint; o caminho sem sufixo é o que a política libera.
    private async Task<KeyedHash> HmacAsync(string key, byte[] input, CancellationToken cancellationToken)
    {
        var response = await vault.SendAsync(
                HttpMethod.Post, $"transit/hmac/{key}", new { input = Convert.ToBase64String(input) }, cancellationToken)
            ?? throw new InvalidOperationException($"OpenBao has no transit key {key}.");
        return Parse(response.GetProperty("data").GetProperty("hmac").GetString() ?? string.Empty);
    }

    // `vault:v<n>:<base64>`: guardados os 32 bytes e `n` em separado, para a coluna única comparar só o valor.
    internal static KeyedHash Parse(string hmac)
    {
        var separator = hmac.IndexOf(':', Prefix.Length);
        if (!hmac.StartsWith(Prefix, StringComparison.Ordinal) || separator < 0
            || !int.TryParse(hmac.AsSpan(Prefix.Length, separator - Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new FormatException("OpenBao returned an HMAC outside the vault:v<n>:<base64> format.");
        }

        var value = Convert.FromBase64String(hmac[(separator + 1)..]);
        return value.Length == 32 ? new KeyedHash(value, version) : throw new FormatException("OpenBao returned an HMAC that is not 32 bytes.");
    }
}
