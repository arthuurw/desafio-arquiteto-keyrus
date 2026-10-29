using System.Diagnostics;
using System.Text;

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
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new ProcessResult(process.ExitCode, output.ToString());
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }
    }
}
