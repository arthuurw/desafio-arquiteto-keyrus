using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace IntegrationTests.Infrastructure;

// OpenBao real em modo servidor, com o mesmo `config.hcl`, a mesma política e o mesmo script de bootstrap do compose
// (ADR-0016). A porta do host é fixa por instância: parado e iniciado de novo, o cofre volta no mesmo endereço.
internal sealed class OpenBaoVault : IAsyncDisposable
{
    internal const string Image = "openbao/openbao:2.7.2";
    internal const string RoleId = "customers-api-role_dev";
    internal const string SecretId = "customers-api-secret_dev";

    // Chave do seal estático do ambiente local, 32 bytes; a mesma do docker-compose.
    internal const string UnsealKey = "localdev-openbao-static-seal_dev";

    private const int ApiPort = 8200;
    private const string BootstrapDir = "/tmp/openbao-bootstrap";

    private readonly IContainer _container;
    private readonly HttpClient _http;
    private string _rootToken = null!;

    private OpenBaoVault()
    {
        HostPort = FreePort();
        _container = new ContainerBuilder(Image)
            .WithPortBinding(HostPort, ApiPort)
            .WithEnvironment("BAO_STATIC_UNSEAL_KEY", UnsealKey)
            .WithResourceMapping(new FileInfo(Path.Combine(Repository.OpenBaoDirectory, "config.hcl")), "/openbao/config/")
            .WithResourceMapping(new FileInfo(Path.Combine(Repository.OpenBaoDirectory, "bootstrap.sh")), "/bootstrap/")
            .WithResourceMapping(new FileInfo(Path.Combine(Repository.OpenBaoDirectory, "customers-app.hcl")), "/bootstrap/")
            .WithCommand("server")
            .Build();
        Address = $"http://127.0.0.1:{HostPort}";
        _http = new HttpClient { BaseAddress = new Uri(Address) };
    }

    internal int HostPort { get; }

    internal string Address { get; }

    internal static async Task<OpenBaoVault> StartAsync()
    {
        var vault = new OpenBaoVault();
        await vault._container.StartAsync(TestContext.Current.CancellationToken);
        var bootstrap = await vault.BootstrapAsync();
        bootstrap.ExitCode.ShouldBe(0, bootstrap.Stdout + bootstrap.Stderr);
        var init = Encoding.UTF8.GetString(await vault._container.ReadFileAsync($"{BootstrapDir}/init.json", TestContext.Current.CancellationToken));
        vault._rootToken = JsonDocument.Parse(init).RootElement.GetProperty("root_token").GetString()!;
        return vault;
    }

    // O script do compose, dentro do próprio container do cofre.
    internal Task<ExecResult> BootstrapAsync() =>
        _container.ExecAsync(
            [
                "env", $"BAO_ADDR=http://127.0.0.1:{ApiPort}", $"CUSTOMERS_ROLE_ID={RoleId}", $"CUSTOMERS_SECRET_ID={SecretId}",
                $"BOOTSTRAP_DIR={BootstrapDir}", "sh", "/bootstrap/bootstrap.sh",
            ],
            TestContext.Current.CancellationToken);

    internal Task<ExecResult> StatusAsync() =>
        _container.ExecAsync(["env", $"BAO_ADDR=http://127.0.0.1:{ApiPort}", "bao", "status"], TestContext.Current.CancellationToken);

    // A DEK do cliente lida com o token root, ou null quando não existe.
    internal async Task<byte[]?> DekAsync(Guid customerId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"customers/data/dek/{customerId:D}", _rootToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var body = await JsonAsync(response);
        return Convert.FromBase64String(body.GetProperty("data").GetProperty("data").GetProperty("key").GetString()!);
    }

    // Apaga todas as versões da DEK com o token root: a política da Api não deixa, só o operador.
    internal async Task DestroyDekAsync(Guid customerId)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"customers/metadata/dek/{customerId:D}", _rootToken);
        response.EnsureSuccessStatusCode();
    }

    // Ids com DEK no cofre.
    internal async Task<IReadOnlyList<string>> DekIdsAsync()
    {
        using var response = await SendAsync(new HttpMethod("LIST"), "customers/metadata/dek", _rootToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        var body = await JsonAsync(response);
        return body.GetProperty("data").GetProperty("keys").EnumerateArray().Select(key => key.GetString()!).ToList();
    }

    // HMAC calculado pelo teste, com o token root, no caminho com o algoritmo explícito.
    internal async Task<(byte[] Value, int Version)> HmacAsync(string key, byte[] input)
    {
        using var response = await SendAsync(
            HttpMethod.Post, $"transit/hmac/{key}/sha2-256", _rootToken, new { input = Convert.ToBase64String(input) });
        var hmac = (await JsonAsync(response)).GetProperty("data").GetProperty("hmac").GetString()!;
        var parts = hmac.Split(':');
        return (Convert.FromBase64String(parts[2]), int.Parse(parts[1].TrimStart('v'), System.Globalization.CultureInfo.InvariantCulture));
    }

    internal async Task RotateAsync(string key)
    {
        using var response = await SendAsync(HttpMethod.Post, $"transit/keys/{key}/rotate", _rootToken);
        response.EnsureSuccessStatusCode();
    }

    internal async Task<int> LatestVersionAsync(string key)
    {
        using var response = await SendAsync(HttpMethod.Get, $"transit/keys/{key}", _rootToken);
        return (await JsonAsync(response)).GetProperty("data").GetProperty("latest_version").GetInt32();
    }

    // Token do AppRole da Api, como ela faz o login.
    internal async Task<string> AppRoleTokenAsync()
    {
        using var response = await SendAsync(HttpMethod.Post, "auth/approle/login", null, new { role_id = RoleId, secret_id = SecretId });
        return (await JsonAsync(response)).GetProperty("auth").GetProperty("client_token").GetString()!;
    }

    internal Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri($"/v1/{path}", UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Add("X-Vault-Token", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // Pausado, o cofre aceita a conexão TCP e não responde; com o Docker carregado, congelar às vezes falha e passa
    // na tentativa seguinte.
    internal async Task<IAsyncDisposable> PauseAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _container.PauseAsync(TestContext.Current.CancellationToken);
                return new Resume(() => _container.UnpauseAsync(CancellationToken.None));
            }
            catch (Exception) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
            }
        }
    }

    // Parado, a conexão é recusada; de volta, o cofre se deslacra sozinho.
    internal async Task<IAsyncDisposable> StopAsync()
    {
        await _container.StopAsync(TestContext.Current.CancellationToken);
        return new Resume(WaitStartedAsync);
    }

    internal async Task RestartAsync()
    {
        await _container.StopAsync(TestContext.Current.CancellationToken);
        await WaitStartedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _container.DisposeAsync();
    }

    private async Task WaitStartedAsync()
    {
        await _container.StartAsync(CancellationToken.None);
        await Eventually.WaitUntilAsync(
            async () =>
            {
                try
                {
                    using var response = await _http.GetAsync(new Uri("/v1/sys/health", UriKind.Relative), CancellationToken.None);
                    return response.StatusCode == HttpStatusCode.OK;
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(30),
            () => "OpenBao never came back unsealed",
            TimeSpan.FromMilliseconds(100));
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.ShouldBeTrue(text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class Resume(Func<Task> resume) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await resume();
    }
}
