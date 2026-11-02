using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Audit;

// Abre o registro de toda rota auditada e, na volta, grava o desfecho que ninguém gravou: recusa, repetição, falha
// transitória, `401`, `403`, `500` da exceção ou `499` do cliente que desistiu antes da resposta (ADR-0041). A falha
// dessa gravação vai para o log e não muda a resposta já decidida.
internal sealed partial class AuditMiddleware(RequestDelegate next, ILogger<AuditMiddleware> logger)
{
    private const int ClientClosedRequest = StatusCodes.Status499ClientClosedRequest;

    public async Task InvokeAsync(HttpContext context, AuditEvent audit)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<AuditedRoute>() is not { } route)
        {
            await next(context);
            return;
        }

        audit.Begin(route, context);
        try
        {
            await next(context);
        }
        catch (Exception)
        {
            if (!audit.Recorded)
            {
                await WriteOutcomeAsync(context, audit, context.RequestAborted.IsCancellationRequested
                    ? ClientClosedRequest
                    : StatusCodes.Status500InternalServerError);
            }

            throw;
        }

        // Com a resposta já começada, o status foi devolvido: a conexão que cai depois não faz dele um `499`.
        if (!audit.Recorded)
        {
            await WriteOutcomeAsync(context, audit, context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted
                ? ClientClosedRequest
                : context.Response.StatusCode);
        }
    }

    // Escopo novo: o `DbContext` da requisição pode ter ficado com a conexão de um comando cancelado. Sem o token da
    // requisição, que no `499` já está cancelado.
    private async Task WriteOutcomeAsync(HttpContext context, AuditEvent audit, int result)
    {
        try
        {
            await using var scope = context.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var db = (DbContext)scope.ServiceProvider.GetRequiredService(audit.Route!.Trail.DbContextType);
            await audit.RecordAsync(db, result, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogNotRecorded(exception, audit.Route!.Operation, result, audit.TraceId);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit event {Operation} with result {Result} was not recorded, traceId {TraceId}")]
    private partial void LogNotRecorded(Exception exception, string operation, int result, string traceId);
}
