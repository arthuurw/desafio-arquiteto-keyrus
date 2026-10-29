namespace SkippedTestProbe;

public sealed class SkippedTest
{
    [Fact(Skip = "probe: a skipped test must fail the run")]
    public void IsSkipped()
    {
    }
}
