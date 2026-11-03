namespace IntegrationTests.Infrastructure;

// Desfaz no `await using` o que o teste mudou no ambiente: retoma um container, remove uma política.
internal sealed class AsyncCleanup(Func<Task> cleanup) : IAsyncDisposable
{
    public async ValueTask DisposeAsync() => await cleanup();
}
