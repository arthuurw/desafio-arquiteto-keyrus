using Customers;
using Ledger;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Positions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddLedgerModule(builder.Configuration);
builder.Services.AddPositionsModule();
builder.Services.AddCustomersModule(builder.Configuration);

var app = builder.Build();

// Liveness: confirma só que o processo responde, sem consultar dependências (ADR-0021).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

await app.RunAsync();
