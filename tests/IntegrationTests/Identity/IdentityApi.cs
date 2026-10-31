extern alias ApiHost;

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.Json;
using BuildingBlocks.Identity;
using IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests.Identity;

// A Api real, com rotas que existem só no host de teste para provar 401, 403 e a negação por padrão.
internal static class IdentityApi
{
    internal const string DebitsRoute = "/test/debits";
    internal const string CreditsRoute = "/test/credits";
    internal const string RouteWithoutPolicy = "/test/without-policy";

    // Produção, ou o ambiente pedido, com o emissor de teste entregue como configuração OIDC no lugar dos metadados remotos.
    internal static WebApplicationFactory<ApiHost::Program> WithTestIssuer(
        string configuredIssuer = TestTokenIssuer.Issuer, string environment = "Production", bool testRoutes = true) =>
        Create(environment, configuredIssuer, metadataAddress: null, testRoutes, services =>
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.Configuration = TestTokenIssuer.Configuration));

    // Desenvolvimento, lendo os metadados do Keycloak por um endereço diferente do emissor público.
    internal static WebApplicationFactory<ApiHost::Program> WithKeycloak(string issuer, string metadataAddress) =>
        Create("Development", issuer, metadataAddress, testRoutes: true, _ => { });

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
        string environment, string issuer, string? metadataAddress, bool testRoutes, Action<IServiceCollection> configureServices) =>
        new WebApplicationFactory<ApiHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment)
                .UseSetting("Identity:Issuer", issuer)
                .UseSetting("ConnectionStrings:Ledger", UnreachableDatabase.ConnectionString)
                .UseSetting("ConnectionStrings:Customers", UnreachableDatabase.ConnectionString);
            if (metadataAddress is not null)
            {
                builder.UseSetting("Identity:MetadataAddress", metadataAddress);
            }

            builder.ConfigureTestServices(services =>
            {
                configureServices(services);
                if (testRoutes)
                {
                    services.AddSingleton<IStartupFilter, TestRoutes>();
                }
            });
        });

    // Mapeia as rotas no próprio WebApplication da Api: atravessam o roteamento, a autenticação e a autorização dela.
    private sealed class TestRoutes : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            var observed = new RouteBuilderCapture(app);
            next(observed);
            var routes = observed.Routes ?? throw new InvalidOperationException("The Api did not expose its endpoint route builder.");
            routes.MapGet(DebitsRoute, EchoCaller).RequireScope(Scopes.LedgerDebitsWrite);
            routes.MapGet(CreditsRoute, EchoCaller).RequireScope(Scopes.LedgerCreditsWrite);
            routes.MapGet(RouteWithoutPolicy, () => "reached");
        };

        private static IResult EchoCaller(Caller caller) =>
            Results.Json(new { caller.ClientId, Mode = caller.Mode.ToString(), caller.CustomerId });
    }

    // O WebApplication se registra nas propriedades do pipeline durante a montagem e se remove ao final;
    // não há API pública para alcançá-lo, então o decorador guarda o valor quando ele passa por ali.
    private sealed class RouteBuilderCapture(IApplicationBuilder inner) : IApplicationBuilder
    {
        private readonly CapturingProperties _properties = new(inner.Properties);

        internal IEndpointRouteBuilder? Routes => _properties.Routes;

        public IServiceProvider ApplicationServices
        {
            get => inner.ApplicationServices;
            set => inner.ApplicationServices = value;
        }

        public IFeatureCollection ServerFeatures => inner.ServerFeatures;

        public IDictionary<string, object?> Properties => _properties;

        public IApplicationBuilder Use(Func<RequestDelegate, RequestDelegate> middleware)
        {
            inner.Use(middleware);
            return this;
        }

        public IApplicationBuilder New() => inner.New();

        public RequestDelegate Build() => inner.Build();
    }

    private sealed class CapturingProperties(IDictionary<string, object?> inner) : IDictionary<string, object?>
    {
        internal IEndpointRouteBuilder? Routes { get; private set; }

        public ICollection<string> Keys => inner.Keys;

        public ICollection<object?> Values => inner.Values;

        public int Count => inner.Count;

        public bool IsReadOnly => inner.IsReadOnly;

        public object? this[string key]
        {
            get => inner[key];
            set
            {
                Remember(value);
                inner[key] = value;
            }
        }

        public void Add(string key, object? value)
        {
            Remember(value);
            inner.Add(key, value);
        }

        public void Add(KeyValuePair<string, object?> item) => Add(item.Key, item.Value);

        public void Clear() => inner.Clear();

        public bool Contains(KeyValuePair<string, object?> item) => inner.Contains(item);

        public bool ContainsKey(string key) => inner.ContainsKey(key);

        public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex) => inner.CopyTo(array, arrayIndex);

        public bool Remove(string key) => inner.Remove(key);

        public bool Remove(KeyValuePair<string, object?> item) => inner.Remove(item);

        public bool TryGetValue(string key, [MaybeNullWhen(false)] out object? value) => inner.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private void Remember(object? value)
        {
            if (value is IEndpointRouteBuilder routes and IApplicationBuilder)
            {
                Routes = routes;
            }
        }
    }
}
