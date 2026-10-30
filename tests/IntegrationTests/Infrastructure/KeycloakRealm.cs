using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.Keycloak;

namespace IntegrationTests.Infrastructure;

internal sealed record TokenResponse(int Status, JsonElement Body)
{
    internal string AccessToken => Body.GetProperty("access_token").GetString()!;
}

// Keycloak real com o realm versionado, configurado como no docker-compose.
public sealed class KeycloakRealm : IAsyncLifetime
{
    internal const string Image = "quay.io/keycloak/keycloak:26.7.1";
    // Origem pública do Keycloak, a mesma de `KC_HOSTNAME` no docker-compose.
    internal const string PublicOrigin = "http://localhost:8180";
    internal const string PublicIssuer = $"{PublicOrigin}/realms/ledger";
    private const string AdminUsername = "admin";
    private const string AdminPassword = "admin_dev";

    private readonly KeycloakContainer _container = new KeycloakBuilder(Image)
        .WithResourceMapping(new FileInfo(Repository.KeycloakRealmFile), "/opt/keycloak/data/import/")
        .WithCommand("--import-realm")
        .WithUsername(AdminUsername)
        .WithPassword(AdminPassword)
        .WithEnvironment("KC_HOSTNAME", PublicOrigin)
        .WithEnvironment("KC_HOSTNAME_BACKCHANNEL_DYNAMIC", "true")
        .Build();

    private HttpClient _http = null!;

    // Endereço pelo qual o teste e a Api alcançam o container; difere da porta do emissor público.
    internal Uri InternalAddress => new(_container.GetBaseAddress());

    internal string InternalMetadataAddress => new Uri(InternalAddress, "realms/ledger/.well-known/openid-configuration").ToString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        _http = new HttpClient { BaseAddress = InternalAddress };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _container.DisposeAsync();
    }

    internal Task<TokenResponse> ClientCredentialsAsync(string clientId) => RequestTokenAsync(new()
    {
        ["grant_type"] = "client_credentials",
        ["client_id"] = clientId,
        ["client_secret"] = $"{clientId}_dev",
    });

    // Login do usuário no canal: o token serve só para o channel-app trocar.
    internal Task<TokenResponse> ChannelWebLoginAsync(string username) => RequestTokenAsync(new()
    {
        ["grant_type"] = "password",
        ["client_id"] = "channel-web",
        ["username"] = username,
        ["password"] = $"{username}_dev",
    });

    internal Task<TokenResponse> ChannelAppLoginAsync(string username) => RequestTokenAsync(new()
    {
        ["grant_type"] = "password",
        ["client_id"] = "channel-app",
        ["client_secret"] = "channel-app_dev",
        ["username"] = username,
        ["password"] = $"{username}_dev",
    });

    // Autoatendimento como no navegador: login no account-console e gravação pelo Account API.
    internal async Task<int> UpdateOwnAttributeAsync(string username, string attribute, string value)
    {
        var accessToken = await AccountConsoleLoginAsync(username);
        var me = new Dictionary<string, object>
        {
            ["username"] = username,
            ["email"] = $"{username}@ledger.local",
            ["firstName"] = username,
            ["lastName"] = "Teste",
            ["attributes"] = new Dictionary<string, string[]> { [attribute] = [value] },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("realms/ledger/account/", UriKind.Relative))
        {
            Content = JsonContent.Create(me),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // Sem `Accept: application/json` o Keycloak entrega a rota ao console HTML, não ao Account API.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        return (int)response.StatusCode;
    }

    // Configuração efetiva do user profile do realm, lida pela Admin API.
    internal async Task<JsonElement> UserProfileAsync()
    {
        var admin = await RequestTokenAsync(new()
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = AdminUsername,
            ["password"] = AdminPassword,
        }, realm: "master");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("admin/realms/ledger/users/profile", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();
    }

    // Token exchange padrão (RFC 8693) do channel-app para a audiência da Api.
    internal Task<TokenResponse> ExchangeForLedgerApiAsync(string subjectToken, string? scope = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["client_id"] = "channel-app",
            ["client_secret"] = "channel-app_dev",
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["audience"] = "ledger-api",
        };
        if (scope is not null)
        {
            form["scope"] = scope;
        }

        return RequestTokenAsync(form);
    }

    // Authorization code com PKCE no account-console, seguindo o formulário de login sem navegador.
    private async Task<string> AccountConsoleLoginAsync(string username)
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirectUri = $"{PublicOrigin}/realms/ledger/account/";
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var browser = new HttpClient(handler) { BaseAddress = InternalAddress };
        var query = string.Join('&', new Dictionary<string, string>
        {
            ["client_id"] = "account-console",
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        }.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));

        using var loginPage = await browser.GetAsync(
            new Uri($"realms/ledger/protocol/openid-connect/auth?{query}", UriKind.Relative), TestContext.Current.CancellationToken);
        var html = await loginPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // O formulário aponta para o emissor público; o teste alcança o container pela porta mapeada.
        var action = WebUtility.HtmlDecode(Regex.Match(html, "action=\"([^\"]+)\"").Groups[1].Value)
            .Replace($"{PublicOrigin}/", InternalAddress.ToString(), StringComparison.Ordinal);
        // Os cookies de sessão do Keycloak são `Secure`; um cliente HTTP comum não os devolveria por http.
        var cookies = string.Join("; ", loginPage.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(cookie => cookie.Split(';')[0])
            : []);

        using var credentials = new HttpRequestMessage(HttpMethod.Post, new Uri(action))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = $"{username}_dev",
                ["credentialId"] = string.Empty,
            }),
        };
        credentials.Headers.Add("Cookie", cookies);
        using var redirect = await browser.SendAsync(credentials, TestContext.Current.CancellationToken);
        ((int)redirect.StatusCode).ShouldBe(302, html);
        var code = Regex.Match(redirect.Headers.Location!.Query, "[?&]code=([^&]+)").Groups[1].Value;

        var token = await RequestTokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "account-console",
            ["code"] = Uri.UnescapeDataString(code),
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        });
        token.Status.ShouldBe(200, token.Body.ToString());
        return token.AccessToken;
    }

    private async Task<TokenResponse> RequestTokenAsync(Dictionary<string, string> form, string realm = "ledger")
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(
            new Uri($"realms/{realm}/protocol/openid-connect/token", UriKind.Relative), content, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return new TokenResponse((int)response.StatusCode, JsonDocument.Parse(body).RootElement.Clone());
    }
}

[CollectionDefinition(Name)]
public sealed class KeycloakRealmDefinition : ICollectionFixture<KeycloakRealm>
{
    public const string Name = "keycloak-realm";
}
