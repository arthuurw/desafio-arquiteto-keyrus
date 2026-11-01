using System.Net;
using System.Net.Sockets;

namespace IntegrationTests.Infrastructure;

// Servidor TCP que aceita a conexão e nunca responde: o OpenBao que não responde a tempo. Dublê da borda, como o
// `SealedVault`.
internal sealed class SilentVault : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _held = [];
    private readonly Task _accepting;

    internal SilentVault()
    {
        _listener.Start();
        Address = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _accepting = AcceptAsync();
    }

    internal string Address { get; }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
        }

        lock (_held)
        {
            _held.ForEach(client => client.Dispose());
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            lock (_held)
            {
                _held.Add(client);
            }
        }
    }
}
