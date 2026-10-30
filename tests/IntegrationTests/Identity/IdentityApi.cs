extern alias ApiHost;

using System.Net.Http.Headers;
using System.Text.Json;
using BuildingBlocks.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests.Identity;

// A Api real, com rotas que existem só no host de teste para provar 401, 403 e a negação por padrão.
internal static class IdentityApi
{
    internal const string DebitsRoute = "/test/debits";
    internal const string CreditsRoute = "/test/credits";
    internal const string RouteWithoutPolicy = "/test/without-policy";

    // Porta 1: nenhum PostgreSQL escuta ali; nenhuma rota destes testes consulta o banco.
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=ledger_db;Username=ledger_app;Password=ledger_app_dev";

    // Produção, com o emissor de teste entregue como configuração OIDC no lugar dos metadados remotos.
    internal static WebApplicationFactory<ApiHost::Program> WithTestIssuer(string configuredIssuer = TestTokenIssuer.Issuer) =>
        Create("Production", configuredIssuer, metadataAddress: null, services =>
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.Configuration = TestTokenIssuer.Configuration));

    // Desenvolvimento, lendo os metadados do Keycloak por um endereço diferente do emissor público.
    internal static WebApplicationFactory<ApiHost::Program> WithKeycloak(string issuer, string metadataAddress) =>
        Create("Development", issuer, metadataAddress, _ => { });

    internal static async Task<HttpResponseMessage> GetAsync(this HttpClient client, string route, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(route, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    internal static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

    private static WebApplicationFactory<ApiHost::Program> Create(
        string environment, string issuer, string? metadataAddress, Action<IServiceCollection> configureServices) =>
        new WebApplicationFactory<ApiHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment)
                .UseSetting("Identity:Issuer", issuer)
                .UseSetting("ConnectionStrings:Ledger", UnreachableDatabase)
                .UseSetting("ConnectionStrings:Customers", UnreachableDatabase);
            if (metadataAddress is not null)
            {
                builder.UseSetting("Identity:MetadataAddress", metadataAddress);
            }

            builder.ConfigureTestServices(services =>
            {
                configureServices(services);
                services.AddSingleton<IStartupFilter, TestRoutes>();
            });
        });

    // Ramo do pipeline só para /test: autenticação e autorização usam os serviços registrados pela Api.
    private sealed class TestRoutes : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseWhen(context => context.Request.Path.StartsWithSegments("/test", StringComparison.Ordinal), branch =>
            {
                branch.UseRouting();
                branch.UseAuthentication();
                branch.UseAuthorization();
                branch.UseEndpoints(routes =>
                {
                    routes.MapGet(DebitsRoute, Describe).RequireScope("ledger.debits.write");
                    routes.MapGet(CreditsRoute, Describe).RequireScope("ledger.credits.write");
                    routes.MapGet(RouteWithoutPolicy, () => "reached");
                });
            });
            next(app);
        };

        private static IResult Describe(Caller caller) =>
            Results.Json(new { caller.ClientId, Mode = caller.Mode.ToString(), caller.CustomerId });
    }
}
