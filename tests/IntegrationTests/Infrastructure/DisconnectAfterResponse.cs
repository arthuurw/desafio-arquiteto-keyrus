using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;

namespace IntegrationTests.Infrastructure;

// O cliente que fecha a conexão assim que a resposta começa a sair: na requisição com o cabeçalho, o `RequestAborted`
// cancela quando a resposta começa. O `TestServer` não tem conexão para fechar de verdade.
internal sealed class DisconnectAfterResponse : IStartupFilter
{
    internal const string Header = "X-Test-Disconnect-After-Response";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(async (context, pipeline) =>
            {
                if (!context.Request.Headers.ContainsKey(Header))
                {
                    await pipeline(context);
                    return;
                }

                using var disconnect = new CancellationTokenSource();
                context.Features.Set<IHttpRequestLifetimeFeature>(new Lifetime(disconnect.Token));
                context.Response.OnStarting(disconnect.CancelAsync);
                await pipeline(context);
            });
            next(app);
        };

    private sealed class Lifetime(CancellationToken aborted) : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted
        {
            get => aborted;
            set => throw new NotSupportedException();
        }

        public void Abort()
        {
        }
    }
}
