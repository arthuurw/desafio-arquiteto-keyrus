using BuildingBlocks.Health;
using BuildingBlocks.Logging;
using Customers;
using Ledger;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Positions;

var builder = WebApplication.CreateBuilder(args);

builder.AddPersonalDataRedaction();
builder.Services.AddHealthChecks()
    .AddLedgerDatabaseCheck()
    .AddPositionsReadModelCheck(HealthStatus.Unhealthy);
builder.Services.AddLedgerIdempotencyKeyPurge(builder.Configuration);
builder.Services.AddLedgerOutboxRelay(builder.Configuration);
builder.Services.AddCustomersIdempotencyKeyPurge(builder.Configuration);
builder.Services.AddPositionsProjection(builder.Configuration);

var app = builder.Build();

// Liveness: confirma só que o processo responde, sem consultar dependências (ADR-0021).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
// Readiness: o Worker não tem função sem o PostgreSQL e o MongoDB (ADR-0021).
app.MapHealthChecks(Readiness.Route);

await app.RunAsync();
