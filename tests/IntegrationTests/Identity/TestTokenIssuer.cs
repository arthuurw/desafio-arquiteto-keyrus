using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace IntegrationTests.Identity;

// Emissor de teste com chave RSA própria (ADR-0012): a Api o recebe como configuração OIDC e valida como em produção.
internal static class TestTokenIssuer
{
    internal const string Issuer = "https://issuer.test/realms/ledger";
    internal const string KeyId = "test-key";

    internal const string AllScopes =
        "ledger.credits.write ledger.debits.write ledger.entries.reverse ledger.accounts.manage positions.read customers.write customers.read customers.pii.read customers.erasure.manage";

    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = KeyId };

    internal static OpenIdConnectConfiguration Configuration { get; } = CreateConfiguration();

    // Claims do modo serviço, válidas por 300 s a partir de agora.
    internal static Dictionary<string, object> Service(string clientId, string scope)
    {
        var claims = Base(clientId, scope);
        claims["client_id"] = clientId;
        return claims;
    }

    // Claims do modo delegado, como as do token trocado pelo channel-app.
    internal static Dictionary<string, object> Delegated(string customerId, string scope)
    {
        var claims = Base("channel-app", scope);
        claims["customer_id"] = customerId;
        return claims;
    }

    internal static string Sign(Dictionary<string, object> claims) =>
        SignWith(claims, new SigningCredentials(Key, SecurityAlgorithms.RsaSha256));

    internal static string SignWithOtherKey(Dictionary<string, object> claims)
    {
        using var otherRsa = RSA.Create(2048);
        var otherKey = new RsaSecurityKey(otherRsa) { KeyId = KeyId };
        return SignWith(claims, new SigningCredentials(otherKey, SecurityAlgorithms.RsaSha256));
    }

    // Ataque de confusão de algoritmo: HS256 com a chave pública do emissor como segredo.
    internal static string SignWithPublicKeyAsHmacSecret(Dictionary<string, object> claims)
    {
        var publicKey = Encoding.UTF8.GetBytes(Key.Rsa.ExportSubjectPublicKeyInfoPem());
        var hmacKey = new SymmetricSecurityKey(publicKey) { KeyId = KeyId };
        return SignWith(claims, new SigningCredentials(hmacKey, SecurityAlgorithms.HmacSha256));
    }

    internal static string Unsigned(Dictionary<string, object> claims)
    {
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(JsonSerializer.Serialize(claims));
        return $"{header}.{payload}.";
    }

    internal static long UnixSeconds(TimeSpan offsetFromNow) => DateTimeOffset.UtcNow.Add(offsetFromNow).ToUnixTimeSeconds();

    private static Dictionary<string, object> Base(string authorizedParty, string scope) => new()
    {
        ["iss"] = Issuer,
        ["aud"] = new[] { "ledger-api" },
        ["azp"] = authorizedParty,
        ["scope"] = scope,
        ["iat"] = UnixSeconds(TimeSpan.Zero),
        ["nbf"] = UnixSeconds(TimeSpan.Zero),
        ["exp"] = UnixSeconds(TimeSpan.FromSeconds(300)),
    };

    private static string SignWith(Dictionary<string, object> claims, SigningCredentials credentials) =>
        new JsonWebTokenHandler().CreateToken(JsonSerializer.Serialize(claims), credentials);

    private static OpenIdConnectConfiguration CreateConfiguration()
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        configuration.SigningKeys.Add(Key);
        return configuration;
    }
}
