using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace BuildingBlocks.Http;

// Corpo acima de 16 KB é recusado com `413` antes de qualquer leitura do JSON: o maior corpo válido da Api tem menos de
// 1 KB, e o padrão do Kestrel (30 MB) deixaria cada requisição alocar e parsear até isso. Vale para toda rota, também a
// que ainda não existe. Depois da autorização: o `401` e o `403` não mudam, e a trilha registra o client do `413`
// (ADR-0047).
public static class RequestBodyLimit
{
    public const int MaxBytes = 16 * 1024;

    public static IApplicationBuilder UseRequestBodyLimit(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (await IsTooLargeAsync(context))
            {
                await ProblemResponses.Result(ProblemResponses.PayloadTooLarge()).ExecuteAsync(context);
                return;
            }

            await next(context);
        });

    private static async Task<bool> IsTooLargeAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength is { } length)
        {
            return length > MaxBytes;
        }

        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false)
        {
            return false;
        }

        // Sem `Content-Length` (chunked), o tamanho só aparece lendo: lê até um byte além do limite, e o que coube
        // segue para o endpoint no lugar do corpo original.
        var buffer = new byte[MaxBytes + 1];
        var read = 0;
        int chunk;
        while (read < buffer.Length
            && (chunk = await request.Body.ReadAsync(buffer.AsMemory(read), context.RequestAborted)) > 0)
        {
            read += chunk;
        }

        if (read > MaxBytes)
        {
            return true;
        }

        request.Body = new MemoryStream(buffer, 0, read, writable: false);
        return false;
    }
}
