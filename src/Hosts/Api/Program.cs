using System.Diagnostics;
using BuildingBlocks.Identity;
using Customers;
using Ledger;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Positions;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLedgerIdentity(builder.Configuration, builder.Environment);
builder.Services.AddHealthChecks();
builder.Services.AddLedgerModule(builder.Configuration);
builder.Services.AddPositionsModule(builder.Configuration);
builder.Services.AddCustomersModule(builder.Configuration);
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info = new OpenApiInfo
    {
        Title = "Ledger API",
        Version = "v1",
        Description = "Movimentações e saldo de contas. Erros em Problem Details (RFC 9457), tratáveis pelo `type`.",
    };
    // Sem o endereço da requisição que gerou o documento: a cópia versionada é igual em qualquer máquina.
    document.Servers = [];
    document.Components ??= new OpenApiComponents();
    document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
    {
        ["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Token OAuth2 do provedor de identidade, com o escopo que a rota exige.",
        },
    };
    return Task.CompletedTask;
}));

var app = builder.Build();

// Erro não tratado vira Problem Details em todo ambiente, inclusive Development: o detalhe da exceção vai só
// para o log, e a resposta leva o `traceId` (ADR-0039). Antes da autenticação, para cobrir também ela.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    ExceptionHandler = context =>
    {
        var problem = new ProblemDetails
        {
            Type = "internal-error",
            Status = StatusCodes.Status500InternalServerError,
            Title = "Erro interno",
            Detail = "A requisição não pôde ser concluída. Informe o traceId ao suporte.",
        };
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        return TypedResults.Problem(problem).ExecuteAsync(context);
    },
});
app.UseAuthentication();
app.UseAuthorization();

// Liveness: confirma só que o processo responde, sem consultar dependências (ADR-0021).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
    .AllowAnonymous();

// Contrato navegável só em Development; fora dele as rotas não existem e caem na política padrão (ADR-0039).
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

app.MapLedgerEndpoints();
app.MapPositionsEndpoints();

await app.RunAsync();
