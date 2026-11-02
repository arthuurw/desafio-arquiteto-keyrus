using System.Security.Cryptography;

namespace IntegrationTests.Infrastructure;

// Reuso opcional, só na máquina de quem desenvolve: com `LEDGER_TESTS_REUSE=1`, o PostgreSQL e o Keycloak sobrevivem à
// execução e a próxima os encontra de pé, sem esperar a subida. Os dois ficam rodando até alguém removê-los. O CI não
// liga o reuso, e cada execução dele começa do zero.
internal static class ContainerReuse
{
    internal static bool Enabled { get; } = Environment.GetEnvironmentVariable("LEDGER_TESTS_REUSE") == "1";

    // O hash do Testcontainers não olha o conteúdo dos arquivos montados: o rótulo com o hash deles força um container
    // novo quando o script de init ou o realm mudam.
    internal const string SourceLabel = "ledger.tests.source";

    internal static string SourceHash(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
}
