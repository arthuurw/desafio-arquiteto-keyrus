using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace IntegrationTests.Infrastructure;

// `Exception` é o texto da exceção logada junto, quando há: o formatter da mensagem não o inclui.
internal sealed record LogLine(string Category, LogLevel Level, string Message, string? Exception = null);

// Guarda as linhas de log da Api sob teste, para o teste afirmar o que foi logado durante uma requisição.
internal sealed class LogSink : ILoggerProvider
{
    private readonly ConcurrentQueue<LogLine> _lines = new();

    internal int Mark() => _lines.Count;

    // Linhas desde a marca, na ordem em que chegaram.
    internal IReadOnlyList<LogLine> Since(int mark) => _lines.Skip(mark).ToList();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<LogLine> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(new LogLine(category, logLevel, formatter(state, exception), exception?.ToString()));
    }
}
