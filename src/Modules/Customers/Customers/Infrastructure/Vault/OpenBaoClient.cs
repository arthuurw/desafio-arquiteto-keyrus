using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Customers.Infrastructure.Vault;

// Endereço e credenciais do AppRole da Api no OpenBao (ADR-0016). O token root nunca chega à Api.
internal sealed record OpenBaoSettings(Uri Address, string RoleId, string SecretId)
{
    // Acima disso o cofre conta como fora do ar: o cadastro e a leitura respondem `503`, sem esperar mais.
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    internal static OpenBaoSettings Read(IConfiguration configuration)
    {
        var address = configuration["OpenBao:Address"]
            ?? throw new InvalidOperationException("OpenBao:Address is not configured.");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"OpenBao:Address must be an absolute URI; got '{address}'.");
        }

        return new OpenBaoSettings(
            uri,
            configuration["OpenBao:RoleId"] ?? throw new InvalidOperationException("OpenBao:RoleId is not configured."),
            configuration["OpenBao:SecretId"] ?? throw new InvalidOperationException("OpenBao:SecretId is not configured."));
    }
}

// O cofre não respondeu, ou respondeu com falha que não é do pedido: a operação com dado pessoal não pode seguir.
internal sealed class PersonalDataUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

// HTTP do OpenBao com o token do AppRole, renovado pelo login quando expira ou é recusado. Toda falha de rede, timeout
// ou erro do servidor vira `PersonalDataUnavailableException`; o corpo de erro do cofre nunca vai para a mensagem.
internal sealed class OpenBaoClient(HttpClient http, OpenBaoSettings settings, TimeProvider clock) : IDisposable
{
    private const string TokenHeader = "X-Vault-Token";

    // Renova antes do fim do TTL: um token que expira no meio de uma requisição não derruba o cadastro.
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _login = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    // `null` quando o caminho não existe (`404`); `403` com token válido é recusa da política e sobe como erro.
    internal async Task<JsonElement?> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(renew: false, cancellationToken);
        using var response = await SendRawAsync(method, path, body, token, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // Token revogado ou expirado antes do previsto: um login novo e uma segunda tentativa.
            token = await TokenAsync(renew: true, cancellationToken);
            using var retried = await SendRawAsync(method, path, body, token, cancellationToken);
            return await ReadAsync(retried, path, cancellationToken);
        }

        return await ReadAsync(response, path, cancellationToken);
    }

    private static async Task<JsonElement?> ReadAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            throw new InvalidOperationException($"OpenBao refused {path} with {(int)response.StatusCode}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new PersonalDataUnavailableException($"OpenBao answered {(int)response.StatusCode} for {path}.");
        }

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return default(JsonElement);
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new PersonalDataUnavailableException($"OpenBao did not complete the answer for {path}.", exception);
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method, string path, object? body, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(settings.Address, $"/v1/{path}"));
        if (token is not null)
        {
            request.Headers.Add(TokenHeader, token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // O `TaskCanceledException` sem cancelamento do chamador é o timeout de 1 s do HttpClient.
            throw new PersonalDataUnavailableException($"OpenBao did not answer {path} in time.", exception);
        }
    }

    public void Dispose()
    {
        _login.Dispose();
        http.Dispose();
    }

    private async Task<string> TokenAsync(bool renew, CancellationToken cancellationToken)
    {
        var known = _token;
        if (!renew && known is not null && clock.GetUtcNow() < _expiresAt)
        {
            return known;
        }

        await _login.WaitAsync(cancellationToken);
        try
        {
            // Outro login concluído enquanto este esperava já serve: o token mudou desde a leitura acima.
            if (_token is not null && clock.GetUtcNow() < _expiresAt && (!renew || _token != known))
            {
                return _token;
            }

            using var response = await SendRawAsync(
                HttpMethod.Post, "auth/approle/login", new { role_id = settings.RoleId, secret_id = settings.SecretId }, null, cancellationToken);
            var login = await ReadAsync(response, "auth/approle/login", cancellationToken)
                ?? throw new InvalidOperationException("OpenBao has no AppRole login.");
            var auth = login.GetProperty("auth");
            _token = auth.GetProperty("client_token").GetString()
                ?? throw new InvalidOperationException("OpenBao AppRole login returned no token.");
            _expiresAt = clock.GetUtcNow() + TimeSpan.FromSeconds(auth.GetProperty("lease_duration").GetInt64()) - RenewBefore;
            return _token;
        }
        finally
        {
            _login.Release();
        }
    }
}
