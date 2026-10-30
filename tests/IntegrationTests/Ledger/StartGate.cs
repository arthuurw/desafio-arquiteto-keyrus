namespace IntegrationTests.Ledger;

// Barreira explícita: cada tarefa avisa que está pronta e espera o mesmo sinal; nada depende de espera por tempo.
internal static class StartGate
{
    internal static async Task<T[]> RunTogetherAsync<T>(int count, Func<int, Task<T>> action)
    {
        var pending = count;
        var allReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, count).Select(index => Task.Run(async () =>
        {
            if (Interlocked.Decrement(ref pending) == 0)
            {
                allReady.SetResult();
            }

            await go.Task;
            return await action(index);
        })).ToArray();

        await allReady.Task;
        go.SetResult();
        return await Task.WhenAll(tasks);
    }
}
