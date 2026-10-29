using System.Diagnostics;
using System.Text;

namespace BuildTests;

internal sealed record DotnetResult(int ExitCode, string Output);

// Executa o dotnet CLI real como processo filho, a partir da raiz do repositório.
internal static class Dotnet
{
    internal static string RepositoryRoot { get; } = FindRepositoryRoot();

    internal static async Task<DotnetResult> RunAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Isola o processo filho de variáveis herdadas do runner de testes.
        foreach (var key in startInfo.Environment.Keys.Where(k => k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new DotnetResult(process.ExitCode, output.ToString());
    }

    // Constrói um projeto de prova em tests/Probes, sob as regras de build do repositório.
    internal static Task<DotnetResult> BuildProbeAsync(string probe) =>
        RunAsync(RepositoryRoot, "build", Path.Combine("tests", "Probes", probe), "-c", "Release", "-tl:off", "-nodeReuse:false");

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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ledger.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Ledger.slnx not found above the test output directory.");
    }
}
