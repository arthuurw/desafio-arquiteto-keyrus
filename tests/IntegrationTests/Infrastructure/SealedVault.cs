using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntegrationTests.Infrastructure;

// Servidor que responde `503` a toda requisição, como o OpenBao selado ou em standby: o cofre que responde, mas com
// erro. Dublê da borda, ao lado do `SilentVault`, porque o cofre real selado só abre de novo recriado.
internal sealed class SealedVault : IAsyncDisposable
{
    private static readonly object Sealed = new { errors = new[] { "Vault is sealed" } };
    private readonly WebApplication _app;
    private int _requests;

    internal SealedVault()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Run(async context =>
        {
            Interlocked.Increment(ref _requests);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(Sealed);
        });
    }

    internal string Address => _app.Services.GetRequiredService<IServer>().Features
        .GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();

    internal int Requests => Volatile.Read(ref _requests);

    internal Task StartAsync() => _app.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
