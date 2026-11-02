using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace IntegrationTests.Infrastructure;

// Sinaliza o fim de cada requisição na Api sob teste, pelo trace do `traceparent` que ela recebeu. O cliente recebe a
// resposta antes de o pipeline terminar: só depois deste sinal o teste sabe que nenhuma gravação ainda está por vir.
internal sealed class RequestCompletions : IStartupFilter
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, TaskCompletionSource> _done = new();

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(async (context, pipeline) =>
            {
                try
                {
                    await pipeline(context);
                }
                finally
                {
                    if (TraceOf(context) is { } trace)
                    {
                        Completion(trace).TrySetResult();
                    }
                }
            });
            next(app);
        };

    internal Task WaitAsync(string trace) => Completion(trace).Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);

    private TaskCompletionSource Completion(string trace) =>
        _done.GetOrAdd(trace, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    // `00-<trace>-<span>-<flags>`: o segundo campo.
    private static string? TraceOf(HttpContext context) =>
        context.Request.Headers.TryGetValue("traceparent", out var value) && value.ToString().Split('-') is [_, var trace, _, _]
            ? trace
            : null;
}
