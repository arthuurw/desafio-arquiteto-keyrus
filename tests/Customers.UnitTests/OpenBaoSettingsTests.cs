using Customers.Infrastructure.Vault;
using Microsoft.Extensions.Configuration;

namespace Customers.UnitTests;

public sealed class OpenBaoSettingsTests
{
    [Fact]
    public void MissingTimeoutIsOneSecond() =>
        OpenBaoSettings.Read(Configuration(timeout: null)).Timeout.ShouldBe(TimeSpan.FromSeconds(1));

    [Fact]
    public void ConfiguredTimeoutIsRead() =>
        OpenBaoSettings.Read(Configuration("00:00:02")).Timeout.ShouldBe(TimeSpan.FromSeconds(2));

    // Prazo zero ou negativo deixaria toda chamada ao cofre sem tempo nenhum: a Api não sobe.
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    public void ZeroOrNegativeTimeoutIsRefused(string timeout) =>
        Should.Throw<InvalidOperationException>(() => OpenBaoSettings.Read(Configuration(timeout)))
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
}
