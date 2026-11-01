using Microsoft.AspNetCore.Hosting;

namespace IntegrationTests.Infrastructure;

// Porta 1: nenhum cofre escuta ali. A Api sobe com ele, e só cadastro e leitura de cliente o tocam (ADR-0016).
internal static class UnreachableVault
{
    internal const string Address = "http://127.0.0.1:1";
    internal const string RoleId = "customers-api-role_dev";
    internal const string SecretId = "customers-api-secret_dev";

    internal static IWebHostBuilder WithVault(this IWebHostBuilder builder, string address = Address) => builder
        .UseSetting("OpenBao:Address", address)
        .UseSetting("OpenBao:RoleId", RoleId)
        .UseSetting("OpenBao:SecretId", SecretId);
}
