using System.Text.Json;

namespace BuildTests;

public sealed class BuildQualityTests
{
    [Fact]
    public async Task SolutionBuildsWithZeroWarnings()
    {
        // Saída em diretório próprio: não disputa arquivos com o build em andamento.
        var artifacts = Path.Combine(Path.GetTempPath(), "ledger-build-" + Guid.NewGuid().ToString("N"));

        var result = await Dotnet.RunAsync(Dotnet.RepositoryRoot, "build", "Ledger.slnx", "-c", "Release", "-tl:off", "-nodeReuse:false", "--artifacts-path", artifacts);

        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldContain("0 Warning(s)");
        result.Output.ShouldContain("0 Error(s)");
    }

    [Theory]
    [InlineData("CS0168")]
    [InlineData("CS8602")]
    [InlineData("CA1305")]
    public async Task WarningProbeFailsBuild(string expectedError)
    {
        var result = await Dotnet.BuildProbeAsync("WarningProbe");

        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain($"error {expectedError}");
    }

    [Fact]
    public async Task StyleProbeFailsBuild()
    {
        var result = await Dotnet.BuildProbeAsync("StyleProbe");

        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("error IDE0161");
    }

    [Fact]
    public async Task VulnerablePackageProbeFailsBuild()
    {
        var result = await Dotnet.BuildProbeAsync("VulnerablePackageProbe");

        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("error NU1903");
        result.Output.ShouldContain("Newtonsoft.Json");
    }

    [Fact]
    public async Task LowSeverityPackageProbeFailsBuild()
    {
        var result = await Dotnet.BuildProbeAsync("LowSeverityPackageProbe");

        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("error NU1901: Warning As Error: Package 'NuGet.Packaging' 4.9.6 has a known low severity vulnerability");
    }

    [Fact]
    public async Task InlinePackageVersionProbeFailsBuild()
    {
        var result = await Dotnet.BuildProbeAsync("InlinePackageVersionProbe");

        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("error NU1008");
    }

    [Fact]
    public async Task SkippedTestProbeFailsTestRun()
    {
        var result = await Dotnet.RunAsync(Dotnet.RepositoryRoot, "test", Path.Combine("tests", "Probes", "SkippedTestProbe"), "-c", "Release");

        result.ExitCode.ShouldNotBe(0, result.Output);
        // O teste chegou a executar: a falha vem do teste pulado, não do build nem de argumento inválido.
        result.Output.ShouldContain("total: 1");
        result.Output.ShouldContain("FAIL_SKIP");
    }

    [Fact]
    public async Task GlobalJsonPinsSdk10()
    {
        using var globalJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Dotnet.RepositoryRoot, "global.json"), TestContext.Current.CancellationToken));
        var sdk = globalJson.RootElement.GetProperty("sdk");

        var result = await Dotnet.RunAsync(Dotnet.RepositoryRoot, "--version");

        sdk.GetProperty("version").GetString().ShouldBe("10.0.300");
        sdk.GetProperty("rollForward").GetString().ShouldBe("latestFeature");
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.Trim().ShouldStartWith("10.0.");
    }
}
