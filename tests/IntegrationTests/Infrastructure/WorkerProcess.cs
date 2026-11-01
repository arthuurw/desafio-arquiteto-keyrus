using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace IntegrationTests.Infrastructure;

// Executa o Worker compilado como processo real, com a configuração vinda do ambiente, como no container.
internal sealed class WorkerProcess : IDisposable
{
    private readonly Process _process;
    private readonly ProcessOutput _output;

    private WorkerProcess(IReadOnlyDictionary<string, string> settings)
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
        // Sem MongoDB por padrão: só o ambiente de Positions passa um, para a projeção.
        startInfo.Environment["ConnectionStrings__Positions"] = UnreachableDatabase.MongoConnectionString;
        // Sem o banco de Customers por padrão: a limpeza dele só registra a falha, e o Worker segue de pé.
        startInfo.Environment["ConnectionStrings__Customers"] = UnreachableDatabase.ConnectionString;
        foreach (var (key, value) in settings)
        {
            startInfo.Environment[key] = value;
        }

        _process = new Process { StartInfo = startInfo };
        _output = ProcessOutput.Capture(_process);
    }

    internal int Port { get; }

    internal string Output => _output.ToString();

    internal bool HasExited => _process.HasExited;

    internal async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(timeout);
        await _process.WaitForExitAsync(cancellation.Token);
        return _process.ExitCode;
    }

    internal static WorkerProcess Start(string connectionString, TimeSpan? purgeInterval = null) =>
        Start(connectionString, purgeInterval?.ToString("c"));

    // O intervalo como texto, para provar também valores que nem são um intervalo.
    internal static WorkerProcess Start(string connectionString, string? purgeInterval)
    {
        var settings = new Dictionary<string, string> { ["ConnectionStrings__Ledger"] = connectionString };
        if (purgeInterval is not null)
        {
            settings["Idempotency__PurgeInterval"] = purgeInterval;
        }

        return Start(settings);
    }

    // Qualquer configuração, na forma de variável de ambiente (`Secao__Chave`).
    internal static WorkerProcess Start(IReadOnlyDictionary<string, string> settings)
    {
        var worker = new WorkerProcess(settings);
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
