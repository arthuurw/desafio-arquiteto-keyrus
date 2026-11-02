using System.Text.RegularExpressions;

namespace BuildTests;

// Os testes de integração sobem as mesmas imagens do compose, byte a byte: tag e digest iguais.
public sealed partial class ImagePinningTests
{
    private static readonly string[] Images = ["postgres", "mongo", "quay.io/keycloak/keycloak", "openbao/openbao"];

    [Fact]
    public void TestcontainersImagesMatchCompose()
    {
        var compose = ComposeImage().Matches(File.ReadAllText(Path.Combine(Dotnet.RepositoryRoot, "docker-compose.yml")))
            .Select(match => match.Groups["ref"].Value)
            .ToHashSet(StringComparer.Ordinal);
        var tests = Directory.EnumerateFiles(Path.Combine(Dotnet.RepositoryRoot, "tests", "IntegrationTests"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => TestImage().Matches(File.ReadAllText(file)).Select(match => match.Groups["ref"].Value))
            .ToList();

        tests.Select(Repository).Order(StringComparer.Ordinal).ShouldBe(Images.Order(StringComparer.Ordinal));
        foreach (var image in tests)
        {
            image.ShouldContain("@sha256:", Case.Sensitive, image);
            compose.ShouldContain(image, image);
        }
    }

    // `mongo:8.0.34@sha256:...` -> `mongo`; também sem digest, para a falha dizer qual imagem é.
    private static string Repository(string image)
    {
        var at = image.IndexOf('@', StringComparison.Ordinal);
        var tagged = at < 0 ? image : image[..at];
        return tagged[..tagged.LastIndexOf(':')];
    }

    [GeneratedRegex(@"^\s*image:\s*(?<ref>\S+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ComposeImage();

    [GeneratedRegex(@"const string Image = ""(?<ref>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex TestImage();
}
