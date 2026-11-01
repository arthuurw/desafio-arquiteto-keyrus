using System.Net;
using System.Net.Sockets;

namespace IntegrationTests.Infrastructure;

// Proxy na frente do cofre que aceita a conexão e segura a resposta: o OpenBao que não responde a tempo, a única borda
// dublada dos testes de Customers.
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
