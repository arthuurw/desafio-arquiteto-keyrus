using System.Diagnostics;
using System.Text;

namespace IntegrationTests.Infrastructure;

// Junta stdout e stderr de um processo filho; os eventos chegam de threads diferentes.
internal sealed class ProcessOutput
{
    private readonly StringBuilder _output = new();

    internal static ProcessOutput Capture(Process process)
    {
        var output = new ProcessOutput();
        process.OutputDataReceived += (_, e) => output.Append(e.Data);
        process.ErrorDataReceived += (_, e) => output.Append(e.Data);
        return output;
    }

    public override string ToString()
    {
        lock (_output)
        {
            return _output.ToString();
        }
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
