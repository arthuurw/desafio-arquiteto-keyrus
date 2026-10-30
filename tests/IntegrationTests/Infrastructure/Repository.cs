using System.Reflection;

namespace IntegrationTests.Infrastructure;

internal static class Repository
{
    internal static string Root { get; } = FindRoot();

    // Mesmo arquivo montado pelo docker-compose.
    internal static string InitScript => Path.Combine(Root, "docker", "postgres", "init", "01-roles-and-schemas.sql");

    // Mesmo realm importado pelo Keycloak do docker-compose.
    internal static string KeycloakRealmFile => Path.Combine(Root, "docker", "keycloak", "realm-ledger.json");

    internal static string ComposeFile => Path.Combine(Root, "docker-compose.yml");

    internal static string MigratorDll => HostDll("Migrator");

    internal static string ApiDll => HostDll("Api");

    private static string HostDll(string host)
    {
        var configuration = typeof(Repository).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        return Path.Combine(Root, "src", "Hosts", host, "bin", configuration, "net10.0", $"{host}.dll");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ledger.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Ledger.slnx not found above the test output directory.");
    }
}
