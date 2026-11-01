using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BuildingBlocks.Http;
using BuildingBlocks.Identity;
using BuildingBlocks.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Audit;

// O registro de auditoria da requisição, aberto pelo middleware numa rota auditada. Quem grava primeiro marca o registro
// como gravado, e o middleware não grava de novo: uma linha por requisição (ADR-0041).
public sealed class AuditRecord
{
    private AuditedRoute? _route;
    private HttpContext? _context;
    private string _traceId = "";
    private Guid? _createdResource;

    public bool Recorded { get; private set; }

    internal AuditedRoute? Route => _route;

    internal string TraceId => _traceId;

    // O `traceId` é lido aqui, com a atividade da requisição ainda corrente.
    internal void Begin(AuditedRoute route, HttpContext context)
    {
        _route = route;
        _context = context;
        _traceId = ProblemResponses.TraceId(context);
    }

    // Na rota que cria o recurso, o id dele vem do corpo do `201`, também numa repetição.
    internal void Observe(WriteResult result)
    {
        if (_route is { ResourceRouteValue: null } && result.Status == StatusCodes.Status201Created)
        {
            using var body = JsonDocument.Parse(result.Body);
            _createdResource = body.RootElement.TryGetProperty("id", out var id) && id.TryGetGuid(out var created) ? created : null;
        }
    }

    // Insere o registro no banco do `db`, na transação aberta nele quando há uma; quem confirma marca com `MarkRecorded`.
    public async Task WriteAsync(DbContext db, int result, CancellationToken cancellationToken)
    {
        if (_route is not { } route || _context is not { } context)
        {
            return;
        }

        var caller = Caller.FromPrincipal(context.User);
        await db.Database.ExecuteSqlAsync(
            FormattableStringFactory.Create(
                $"INSERT INTO {route.Trail.Table} "
                    + "(id, client_id, caller_customer_id, operation, resource_type, resource_id, result, trace_id) "
                    + "VALUES ({0}, {1}::text, {2}::uuid, {3}, {4}, {5}::uuid, {6}, {7})",
                Guid.CreateVersion7(),
                caller?.ClientId,
                caller?.CustomerId,
                route.Operation,
                route.ResourceType,
                ResourceId(route, context),
                result,
                _traceId),
            cancellationToken);
    }

    public void MarkRecorded() => Recorded = true;

    // Grava fora de transação e marca: o registro já está no banco quando volta.
    public async Task RecordAsync(DbContext db, int result, CancellationToken cancellationToken)
    {
        await WriteAsync(db, result, cancellationToken);
        MarkRecorded();
    }

    // Id fora do formato não vai para a trilha: pode ser qualquer texto que o chamador mandou, até um CPF.
    private Guid? ResourceId(AuditedRoute route, HttpContext context) =>
        route.ResourceRouteValue is { } name
            ? context.Request.RouteValues[name] is string value
                && Guid.TryParse(value, CultureInfo.InvariantCulture, out var id) ? id : null
            : _createdResource;
}
