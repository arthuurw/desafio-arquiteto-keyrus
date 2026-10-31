using IntegrationTests.Infrastructure;

namespace IntegrationTests.Hosts;

public sealed class WorkerStartupTests
{
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("1.00:00:01")]
    [InlineData("abc")]
    public async Task RejectsInvalidPurgeInterval(string interval)
    {
        using var worker = WorkerProcess.Start(UnreachableDatabase.ConnectionString, interval);

        var exitCode = await worker.WaitForExitAsync(TimeSpan.FromSeconds(60));

        exitCode.ShouldNotBe(0, worker.Output);
        worker.Output.ShouldContain("Idempotency:PurgeInterval");
        worker.Output.ShouldNotContain("Now listening on");
    }

    // As duas pontas da faixa aceita.
    [Theory]
    [InlineData("00:00:01")]
    [InlineData("1.00:00:00")]
    public async Task AcceptsPurgeIntervalAtRangeBounds(string interval)
    {
        using var worker = WorkerProcess.Start(UnreachableDatabase.ConnectionString, interval);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{worker.Port}") };
        await worker.WaitForOutputAsync(output => output.Contains("Now listening on", StringComparison.Ordinal), TimeSpan.FromSeconds(60));

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(200, worker.Output);
        worker.Output.ShouldContain($"Idempotency key purge scheduled every {interval}");
    }
}
