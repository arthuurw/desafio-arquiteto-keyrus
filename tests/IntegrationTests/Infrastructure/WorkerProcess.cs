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
            // No Windows, um console só dele: o evento de `SignalStopAsync` chega a este Worker e a nenhum outro processo.
            CreateNoWindow = true,
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

    // O encerramento que o container recebe: SIGTERM no Linux; no Windows, Ctrl+Break no console do Worker, mandado por
    // um processo auxiliar que se liga a ele, para o teste não se desligar do próprio console. Ctrl+Break e não Ctrl+C:
    // o "ignorar Ctrl+C" passa de pai para filho, e o Worker iniciado aqui não receberia o Ctrl+C. O Ctrl+Break encerra
    // o host como o SIGTERM. Volta com o sinal enviado.
    internal async Task SignalStopAsync()
    {
        var pid = _process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell", ["-NoProfile", "-NonInteractive", "-Command", StopScript.Replace("{pid}", pid, StringComparison.Ordinal)])
            : new ProcessStartInfo("kill", ["-TERM", pid]);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using var signal = Process.Start(startInfo).ShouldNotBeNull();
        var output = await signal.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)
            + await signal.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await signal.WaitForExitAsync(TestContext.Current.CancellationToken);
        signal.ExitCode.ShouldBe(0, output);
    }

    // Solta o console próprio, liga-se ao do Worker e manda o Ctrl+Break (evento 1) para o console, ignorando-o em si.
    private const string StopScript = """
        $ErrorActionPreference = 'Stop'
        Add-Type -Namespace Native -Name Console -MemberDefinition @'
        public delegate bool Handler(uint ctrlType);
        public static readonly Handler Swallow = delegate { return true; };
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(Handler handler, bool add);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
        '@
        [Native.Console]::FreeConsole() | Out-Null
        if (-not [Native.Console]::AttachConsole({pid})) { throw "AttachConsole failed" }
        if (-not [Native.Console]::SetConsoleCtrlHandler([Native.Console]::Swallow, $true)) { throw "SetConsoleCtrlHandler failed" }
        if (-not [Native.Console]::GenerateConsoleCtrlEvent(1, 0)) { throw "GenerateConsoleCtrlEvent failed" }
        Start-Sleep -Milliseconds 200
        """;

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
