using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace IntegrationTests.Infrastructure;

internal sealed record AuditRow(
    string? ClientId, Guid? CallerCustomerId, string Operation, string ResourceType, Guid? ResourceId, int Result, string TraceId)
{
    // Põe no lugar do `trace_id` já conferido contra a resposta: a linha esperada compara só o resto.
    internal const string TraceChecked = "checked";

    internal static AuditRow Expected(
        string? client, Guid? callerCustomer, string operation, string resourceType, Guid? resource, int result) =>
        new(client, callerCustomer, operation, resourceType, resource, result, TraceChecked);
}

// Resposta de uma requisição enviada com `traceparent` próprio, lida depois de a Api terminar a requisição.
internal sealed record TracedResponse(int Status, string Body, string Trace, bool Replayed, IReadOnlyDictionary<string, string> Headers)
{
    // O `traceId` do corpo de erro; o `200` e o `201` não o têm.
    internal string? BodyTraceId =>
        Status >= 400 && Body.Length > 0 && JsonDocument.Parse(Body).RootElement.TryGetProperty("traceId", out var traceId)
            ? traceId.GetString()
            : null;

    internal string? ProblemType =>
        Status >= 400 && Body.Length > 0 && JsonDocument.Parse(Body).RootElement.TryGetProperty("type", out var type)
            ? type.GetString()
            : null;

    internal Guid CreatedId => JsonDocument.Parse(Body).RootElement.GetProperty("id").GetGuid();
}

internal static class AuditRows
{
    internal static string NewTrace() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    internal static HttpRequestMessage TracedRequest(HttpMethod method, string route, string? json, string? token, string? idempotencyKey, string trace)
    {
        var request = new HttpRequestMessage(method, new Uri(route, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        request.Headers.TryAddWithoutValidation("traceparent", $"00-{trace}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}-01");
        return request;
    }

    // Envia com um trace novo e só volta quando a Api terminou a requisição, gravações de auditoria inclusive.
    internal static async Task<TracedResponse> SendTracedAsync(
        this HttpClient client,
        RequestCompletions completions,
        HttpMethod method,
        string route,
        object? body,
        string? token,
        string? idempotencyKey = null,
        string? rawJson = null,
        string? trace = null)
    {
        trace ??= NewTrace();
        var json = rawJson ?? (body is null ? null : JsonSerializer.Serialize(body));
        using var request = TracedRequest(method, route, json, token, idempotencyKey, trace);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        await completions.WaitAsync(trace);
        var headers = response.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value));
        var replayed = headers.TryGetValue("Idempotent-Replayed", out var value) && value == "true";
        return new TracedResponse((int)response.StatusCode, text, trace, replayed, headers);
    }

    // Linhas da trilha do schema gravadas pela requisição do trace.
    internal static async Task<List<AuditRow>> AuditRowsAsync(this PostgresDatabase database, string schema, string trace)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT client_id, caller_customer_id, operation, resource_type, resource_id, result, trace_id
            FROM {schema}.audit_events WHERE trace_id LIKE @trace ORDER BY occurred_at
            """,
            connection);
        command.Parameters.AddWithValue("trace", $"00-{trace}-%");
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new AuditRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetInt32(5),
                reader.GetString(6)));
        }

        return rows;
    }

    // A única linha da requisição: `trace_id` filho do trace enviado e igual ao `traceId` do corpo de erro, se houver.
    internal static async Task<AuditRow> SingleAuditRowAsync(this PostgresDatabase database, string schema, TracedResponse response)
    {
        var rows = await database.AuditRowsAsync(schema, response.Trace);
        var row = rows.ShouldHaveSingleItem($"{schema}.audit_events rows of a {response.Status} response: {string.Join("; ", rows)}");
        row.TraceId.ShouldStartWith($"00-{response.Trace}-");
        response.BodyTraceId.ShouldBeOneOf([null, row.TraceId]);

        return row;
    }

    // A linha de `SingleAuditRowAsync`, com o `trace_id` conferido trocado por `AuditRow.TraceChecked`.
    internal static async Task<AuditRow> CheckedAuditRowAsync(this PostgresDatabase database, string schema, TracedResponse response) =>
        await database.SingleAuditRowAsync(schema, response) with { TraceId = AuditRow.TraceChecked };

    internal static string NewKey() => Guid.NewGuid().ToString();

    // Linhas das duas trilhas de todos os traces dados.
    internal static async Task<long> AuditRowCountAsync(this PostgresDatabase database, IEnumerable<string> traces)
    {
        var count = 0L;
        foreach (var trace in traces)
        {
            count += (await database.AuditRowsAsync("ledger", trace)).Count + (await database.AuditRowsAsync("customers", trace)).Count;
        }

        return count;
    }

    // Trigger de teste que recusa o `INSERT` na trilha do schema para um recurso; descartado, sai do banco.
    internal static async Task<IAsyncDisposable> RefuseAuditInsertAsync(this PostgresDatabase database, string schema, Guid resourceId) =>
        await TestTrigger.CreateAsync(database, $"{schema}.audit_events", "INSERT", $"NEW.resource_id = '{resourceId}'");

    // Recusa toda linha da trilha gravada pela requisição do trace: para rotas cujo id não chega à trilha.
    internal static async Task<IAsyncDisposable> RefuseAuditInsertForTraceAsync(this PostgresDatabase database, string schema, string trace) =>
        await TestTrigger.CreateAsync(database, $"{schema}.audit_events", "INSERT", $"NEW.trace_id LIKE '00-{trace}-%'");

    // Recusa só a linha da escrita confirmada (`201`) da requisição do trace: a linha do desfecho, gravada depois, passa.
    internal static async Task<IAsyncDisposable> RefuseConfirmedAuditInsertAsync(this PostgresDatabase database, string schema, string trace) =>
        await TestTrigger.CreateAsync(database, $"{schema}.audit_events", "INSERT", $"NEW.trace_id LIKE '00-{trace}-%' AND NEW.result = 201");
}

// Trigger `BEFORE` de teste que lança erro para as linhas da condição, criado e removido pelo superusuário.
internal sealed class TestTrigger : IAsyncDisposable
{
    private readonly PostgresDatabase _database;
    private readonly string _table;
    private readonly string _name;

    private TestTrigger(PostgresDatabase database, string table, string name)
    {
        _database = database;
        _table = table;
        _name = name;
    }

    internal static async Task<TestTrigger> CreateAsync(PostgresDatabase database, string table, string operation, string condition)
    {
        var name = $"test_refuse_{Guid.NewGuid():N}";
        var schema = table.Split('.')[0];
        await database.ExecuteAsync("postgres", $"""
            CREATE FUNCTION {schema}.{name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'refused by test trigger {name}';
            END
            $$;
            CREATE TRIGGER {name} BEFORE {operation} ON {table} FOR EACH ROW WHEN ({condition}) EXECUTE FUNCTION {schema}.{name}();
            """);
        return new TestTrigger(database, table, name);
    }

    public async ValueTask DisposeAsync()
    {
        var schema = _table.Split('.')[0];
        await _database.ExecuteAsync("postgres", $"DROP TRIGGER {_name} ON {_table}; DROP FUNCTION {schema}.{_name}();");
    }
}
