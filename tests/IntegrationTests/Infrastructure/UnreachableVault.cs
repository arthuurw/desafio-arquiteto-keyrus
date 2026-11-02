using Customers.Infrastructure.Vault;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntegrationTests.Infrastructure;

// Porta 1: nenhum cofre escuta ali. A Api sobe com ele, e só cadastro e leitura de cliente o tocam (ADR-0016).
internal static class UnreachableVault
{
    internal const string Address = "http://127.0.0.1:1";
    internal const string RoleId = "customers-api-role_dev";
    internal const string SecretId = "customers-api-secret_dev";

    // Fora de Development, a Api só sobe com o cofre por https. O OpenBao dos testes fala http: a subida vê um endereço
    // https, e o endereço real entra depois da checagem, como o emissor de teste entra no lugar dos metadados OIDC.
    // `ApiStartupTests` prova a checagem com a Api como processo, sem este ajuste. Sem prazo, vale o de produção, de 1 s.
    internal static IWebHostBuilder WithVault(this IWebHostBuilder builder, string address = Address, string? timeout = null)
    {
        builder
            .UseSetting("OpenBao:Address", "https://vault.test")
            .UseSetting("OpenBao:RoleId", RoleId)
            .UseSetting("OpenBao:SecretId", SecretId)
            .ConfigureTestServices(services => services.AddSingleton(provider =>
                OpenBaoSettings.Read(provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IHostEnvironment>())
                    with { Address = new Uri(address) }));
        return timeout is null ? builder : builder.UseSetting("OpenBao:Timeout", timeout);
    }
}
