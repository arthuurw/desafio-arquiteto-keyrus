using System.Diagnostics;

namespace IntegrationTests.Infrastructure;

internal sealed record ProcessResult(int ExitCode, string Output);

// Executa o Migrator compilado como processo real, como no docker-compose.
internal static class MigratorProcess
{
    internal static async Task<ProcessResult> RunAsync(string connectionString)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Repository.MigratorDll);
        startInfo.Environment["ConnectionStrings__Migrator"] = connectionString;
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";

        using var process = new Process { StartInfo = startInfo };
        var output = ProcessOutput.Capture(process);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new ProcessResult(process.ExitCode, output.ToString());
    }
}
