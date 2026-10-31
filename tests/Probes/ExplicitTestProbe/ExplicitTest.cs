namespace ExplicitTestProbe;

public sealed class ExplicitTest
{
    [Fact(Explicit = true)]
    public void FailsWhenRun() => Assert.Fail("probe: an explicit test must run in every test run");
}
