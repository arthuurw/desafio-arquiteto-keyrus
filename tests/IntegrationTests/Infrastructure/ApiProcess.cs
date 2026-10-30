using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IntegrationTests.Infrastructure;

// Executa a Api compilada como processo real, com a configuração vinda do ambiente, como no container.
internal sealed class ApiProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();

    private ApiProcess(string environment, string metadataAddress)
    {
        Port = FreePort();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Raiz de conteúdo: os appsettings*.json copiados para a saída do build.
            WorkingDirectory = Path.GetDirectoryName(Repository.ApiDll)!,
        };
        startInfo.ArgumentList.Add(Repository.ApiDll);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{Port}";
        startInfo.Environment["Identity__Issuer"] = "http://localhost:8180/realms/ledger";
        startInfo.Environment["Identity__MetadataAddress"] = metadataAddress;
        startInfo.Environment["ConnectionStrings__Ledger"] = UnreachableDatabase;
        startInfo.Environment["ConnectionStrings__Customers"] = UnreachableDatabase;

        _process = new Process { StartInfo = startInfo };
        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);
    }

    // Porta 1: nenhum PostgreSQL escuta ali; a subida e a liveness não consultam o banco.
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=ledger_db;Username=ledger_app;Password=ledger_app_dev";

    internal int Port { get; }

    internal string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    internal static ApiProcess Start(string environment, string metadataAddress)
    {
        var api = new ApiProcess(environment, metadataAddress);
        api._process.Start();
        api._process.BeginOutputReadLine();
        api._process.BeginErrorReadLine();
        return api;
    }

    internal bool HasExited => _process.HasExited;

    internal async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(timeout);
        await _process.WaitForExitAsync(cancellation.Token);
        return _process.ExitCode;
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }

        _process.Dispose();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_output)
        {
            _output.AppendLine(line);
        }
    }
}
