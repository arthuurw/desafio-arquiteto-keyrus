using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace IntegrationTests.Infrastructure;

// Executa a Api compilada como processo real, com a configuração vinda do ambiente, como no container.
internal sealed class ApiProcess : IDisposable
{
    private readonly Process _process;
    private readonly ProcessOutput _output;

    private ApiProcess(string environment, string metadataAddress, string vaultAddress, IReadOnlyDictionary<string, string>? settings)
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
        startInfo.Environment["Identity__Issuer"] = KeycloakRealm.PublicIssuer;
        startInfo.Environment["Identity__MetadataAddress"] = metadataAddress;
        startInfo.Environment["ConnectionStrings__Ledger"] = UnreachableDatabase.ConnectionString;
        startInfo.Environment["ConnectionStrings__Customers"] = UnreachableDatabase.ConnectionString;
        startInfo.Environment["ConnectionStrings__Positions"] = UnreachableDatabase.MongoConnectionString;
        startInfo.Environment["OpenBao__Address"] = vaultAddress;
        startInfo.Environment["OpenBao__RoleId"] = UnreachableVault.RoleId;
        startInfo.Environment["OpenBao__SecretId"] = UnreachableVault.SecretId;

        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            startInfo.Environment[key] = value;
        }

        _process = new Process { StartInfo = startInfo };
        _output = ProcessOutput.Capture(_process);
    }

    internal int Port { get; }

    internal string Output => _output.ToString();

    internal bool HasExited => _process.HasExited;

    internal static ApiProcess Start(
        string environment, string metadataAddress, string vaultAddress = UnreachableVault.Address, IReadOnlyDictionary<string, string>? settings = null)
    {
        var api = new ApiProcess(environment, metadataAddress, vaultAddress, settings);
        api._process.Start();
        api._process.BeginOutputReadLine();
        api._process.BeginErrorReadLine();
        return api;
    }

    internal async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(timeout);
        await _process.WaitForExitAsync(cancellation.Token);
        return _process.ExitCode;
    }

    // Termina o processo, se ainda no ar, e espera a saída dele inteira chegar a `Output`.
    internal async Task StopAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        await _process.WaitForExitAsync(TestContext.Current.CancellationToken);
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
