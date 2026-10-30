using System.Text.Json;
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
    internal const string PublicIssuer = "http://localhost:8180/realms/ledger";

    private readonly KeycloakContainer _container = new KeycloakBuilder(Image)
        .WithResourceMapping(new FileInfo(Repository.KeycloakRealmFile), "/opt/keycloak/data/import/")
        .WithCommand("--import-realm")
        .WithEnvironment("KC_HOSTNAME", "http://localhost:8180")
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

    internal Task<TokenResponse> ChannelLoginAsync(string username) => RequestTokenAsync(new()
    {
        ["grant_type"] = "password",
        ["client_id"] = "channel-app",
        ["client_secret"] = "channel-app_dev",
        ["username"] = username,
        ["password"] = $"{username}_dev",
    });

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

    private async Task<TokenResponse> RequestTokenAsync(Dictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(
            new Uri("realms/ledger/protocol/openid-connect/token", UriKind.Relative), content, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return new TokenResponse((int)response.StatusCode, JsonDocument.Parse(body).RootElement.Clone());
    }
}

[CollectionDefinition(Name)]
public sealed class KeycloakRealmDefinition : ICollectionFixture<KeycloakRealm>
{
    public const string Name = "keycloak-realm";
}
