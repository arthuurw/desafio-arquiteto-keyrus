using System.Diagnostics;

namespace IntegrationTests.Infrastructure;

// Espera por um estado observável, com teto: a ordem do teste nunca depende de tempo, só a desistência.
internal static class Eventually
{
    // Sem intervalo, cede a vez a cada volta: para estados que duram pouco, como uma requisição parada num lock.
    internal static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan ceiling, Func<string> failure, TimeSpan? interval = null)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            clock.Elapsed.ShouldBeLessThan(ceiling, failure());
            if (interval is { } delay)
            {
                await Task.Delay(delay, TestContext.Current.CancellationToken);
            }
            else
            {
                await Task.Yield();
            }
        }
    }
}
