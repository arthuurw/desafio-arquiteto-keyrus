using BuildingBlocks.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace BuildingBlocks.Identity;

// Autenticação JWT Bearer e autorização por escopo da Api (ADR-0012, ADR-0013).
public static class IdentityExtensions
{
    public const string Audience = "ledger-api";

    public static IServiceCollection AddLedgerIdentity(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var issuer = configuration["Identity:Issuer"]
            ?? throw new InvalidOperationException("Identity:Issuer is not configured.");
        var metadataAddress = configuration["Identity:MetadataAddress"]
            ?? $"{issuer.TrimEnd('/')}/.well-known/openid-configuration";

        // Checado na subida: o JwtBearer só recusaria no primeiro request, com a Api já no ar.
        if (!environment.IsDevelopment()
            && (!Uri.TryCreate(metadataAddress, UriKind.Absolute, out var metadataUri) || metadataUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Identity:MetadataAddress must use https outside Development.");
        }

        services.AddProblemDetails();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // O emissor público pode diferir do endereço interno pelo qual a Api lê os metadados.
                options.MetadataAddress = metadataAddress;
                options.RequireHttpsMetadata = !environment.IsDevelopment();
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = issuer,
                    // O JwtBearer também aceitaria o `issuer` dos metadados; só o emissor configurado vale.
                    IssuerValidator = (tokenIssuer, _, _) => string.Equals(tokenIssuer, issuer, StringComparison.Ordinal)
                        ? tokenIssuer
                        : throw new SecurityTokenInvalidIssuerException("The token issuer is not the configured issuer."),
                    ValidAudience = Audience,
                    // Só RS256: recusa HS256 assinado com a chave pública do emissor (confusão de algoritmo).
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    // Tolerância de relógio reduzida em relação aos 5 min padrão (ADR-0012).
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                options.Events = new JwtBearerEvents
                {
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        await WriteProblemAsync(
                            context.HttpContext,
                            StatusCodes.Status401Unauthorized,
                            "authentication-required",
                            "Autenticação necessária",
                            "A requisição não apresentou um token de acesso válido.");
                    },
                    OnForbidden = context => WriteProblemAsync(
                        context.HttpContext,
                        StatusCodes.Status403Forbidden,
                        "insufficient-scope",
                        "Escopo insuficiente",
                        "O token não concede o acesso exigido por esta rota."),
                };
            });

        // Rota sem política declarada fica inacessível: 401 sem token, 403 com qualquer token.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireAssertion(_ => false).Build());
        services.AddSingleton<AuthorizationMiddlewareResultHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ObservedAuthorizationResultHandler>();

        return services;
    }

    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new ScopeRequirement(scope), new ResolvedCallerRequirement()));

    // O detalhe é o mesmo para toda causa: não revela qual validação falhou.
    private static async Task WriteProblemAsync(HttpContext context, int status, string type, string title, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.WWWAuthenticate = JwtBearerDefaults.AuthenticationScheme;

        var problem = new ProblemDetails { Type = type, Status = status, Title = title, Detail = detail };
        problem.Extensions["traceId"] = ProblemResponses.TraceId(context);
        await TypedResults.Problem(problem).ExecuteAsync(context);
    }
}
