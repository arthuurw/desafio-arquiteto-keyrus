using System.Reflection;

namespace IntegrationTests.Infrastructure;

internal static class Repository
{
    internal static string Root { get; } = FindRoot();

    // Mesmo arquivo montado pelo docker-compose.
    internal static string InitScript => Path.Combine(Root, "docker", "postgres", "init", "01-roles-and-schemas.sql");

    internal static string MigratorDll
    {
        get
        {
            var configuration = typeof(Repository).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
            return Path.Combine(Root, "src", "Hosts", "Migrator", "bin", configuration, "net10.0", "Migrator.dll");
        }
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
