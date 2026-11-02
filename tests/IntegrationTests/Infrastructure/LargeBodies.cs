using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IntegrationTests.Infrastructure;

// Corpos no limite de 16 384 bytes da Api e além dele, com `Content-Length` ou em chunks sem ele.
internal static class LargeBodies
{
    internal const int Limit = 16 * 1024;

    // O JSON do corpo seguido de espaços até `bytes`: continua um JSON válido, só maior.
    internal static string Padded(object body, int bytes)
    {
        var json = JsonSerializer.Serialize(body);
        Encoding.UTF8.GetByteCount(json).ShouldBeLessThanOrEqualTo(bytes);
        return json + new string(' ', bytes - Encoding.UTF8.GetByteCount(json));
    }

    // `bytes` de algo que nem é JSON: o `413` vem antes de qualquer leitura dele.
    internal static string NotJson(int bytes) => new('x', bytes);

    // Sem o redirect automático: com ele, o corpo em chunks chega à Api com `Content-Length` e não passa pela leitura
    // que mede o corpo sem tamanho.
    internal static HttpClient CreateStreamingClient<TEntryPoint>(this WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    internal static async Task<HttpResponseMessage> PostAsync(
        this HttpClient client, string route, string body, string token, string? idempotencyKey = null, bool chunked = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(route, UriKind.Relative))
        {
            Content = chunked ? new ChunkedContent(Encoding.UTF8.GetBytes(body)) : new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (chunked)
        {
            request.Content.Headers.ContentLength.ShouldBeNull();
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // Conteúdo sem tamanho conhecido: vai sem `Content-Length`, como um envio em chunks.
    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] _bytes;

        internal ChunkedContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
