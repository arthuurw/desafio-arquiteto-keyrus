using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace IntegrationTests.Infrastructure;

// Executa o Worker compilado como processo real, com a configuração vinda do ambiente, como no container.
internal sealed class WorkerProcess : IDisposable
{
    private readonly Process _process;
    private readonly ProcessOutput _output;

    private WorkerProcess(string connectionString, TimeSpan? purgeInterval)
    {
        Port = FreePort();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Raiz de conteúdo: os appsettings*.json copiados para a saída do build.
            WorkingDirectory = Path.GetDirectoryName(Repository.WorkerDll)!,
        };
        startInfo.ArgumentList.Add(Repository.WorkerDll);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{Port}";
        startInfo.Environment["ConnectionStrings__Ledger"] = connectionString;
        if (purgeInterval is not null)
        {
            startInfo.Environment["Idempotency__PurgeInterval"] = purgeInterval.Value.ToString("c");
        }

        _process = new Process { StartInfo = startInfo };
        _output = ProcessOutput.Capture(_process);
    }

    internal int Port { get; }

    internal string Output => _output.ToString();

    internal bool HasExited => _process.HasExited;

    internal static WorkerProcess Start(string connectionString, TimeSpan? purgeInterval = null)
    {
        var worker = new WorkerProcess(connectionString, purgeInterval);
        worker._process.Start();
        worker._process.BeginOutputReadLine();
        worker._process.BeginErrorReadLine();
        return worker;
    }

    // Espera pelo log do próprio processo, com teto para um Worker que nunca chega lá.
    internal Task WaitForOutputAsync(Func<string, bool> condition, TimeSpan timeout) =>
        Eventually.WaitUntilAsync(
            () =>
            {
                _process.HasExited.ShouldBeFalse(Output);
                return Task.FromResult(condition(Output));
            },
            timeout,
            () => Output,
            TimeSpan.FromMilliseconds(50));

    internal static int Occurrences(string output, string text)
    {
        var count = 0;
        for (var index = output.IndexOf(text, StringComparison.Ordinal); index >= 0; index = output.IndexOf(text, index + text.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
}
