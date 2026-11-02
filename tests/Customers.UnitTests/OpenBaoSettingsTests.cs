using Customers.Infrastructure.Vault;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Customers.UnitTests;

public sealed class OpenBaoSettingsTests
{
    private static readonly IHostEnvironment Production = new HostEnvironment(Environments.Production);

    [Fact]
    public void MissingTimeoutIsOneSecond() =>
        OpenBaoSettings.Read(Configuration(timeout: null), Production).Timeout.ShouldBe(TimeSpan.FromSeconds(1));

    [Fact]
    public void ConfiguredTimeoutIsRead() =>
        OpenBaoSettings.Read(Configuration("00:00:02"), Production).Timeout.ShouldBe(TimeSpan.FromSeconds(2));

    // Prazo zero ou negativo deixaria toda chamada ao cofre sem tempo nenhum: a Api não sobe.
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    public void ZeroOrNegativeTimeoutIsRefused(string timeout) =>
        Should.Throw<InvalidOperationException>(() => OpenBaoSettings.Read(Configuration(timeout), Production))
            .Message.ShouldContain("OpenBao:Timeout");

    private static IConfiguration Configuration(string? timeout)
    {
        var settings = new Dictionary<string, string?>
        {
            ["OpenBao:Address"] = "https://vault.test",
            ["OpenBao:RoleId"] = "role",
            ["OpenBao:SecretId"] = "secret",
        };
        if (timeout is not null)
        {
            settings["OpenBao:Timeout"] = timeout;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private sealed class HostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Customers.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
