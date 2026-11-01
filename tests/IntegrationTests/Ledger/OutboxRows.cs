using System.Text.Json;
using IntegrationTests.Infrastructure;
using Npgsql;

namespace IntegrationTests.Ledger;

internal sealed record OutboxRow(long Id, Guid AccountId, long Sequence, string EventType, JsonElement Payload, DateTime? ProcessedAt);

// Leitura direta de `ledger.outbox` como superusuário: o efeito observável da escrita do evento.
internal static class OutboxRows
{
    internal static async Task<List<OutboxRow>> OutboxAsync(this PostgresDatabase database, Guid accountId)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id, account_id, sequence, event_type, payload::text, processed_at FROM ledger.outbox WHERE account_id = @id ORDER BY id",
            connection);
        command.Parameters.AddWithValue("id", accountId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<OutboxRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new OutboxRow(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetInt64(2),
                reader.GetString(3),
                JsonDocument.Parse(reader.GetString(4)).RootElement.Clone(),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5)));
        }

        return rows;
    }

    // O payload descreve o lançamento gravado: mesmos valores, campo a campo.
    internal static void ShouldDescribe(this OutboxRow row, EntryRow entry, Guid accountId)
    {
        var context = $"outbox {row.Id}: {row.Payload}";
        row.EventType.ShouldBe("EntryRecorded.v1", context);
        row.AccountId.ShouldBe(accountId, context);
        row.Sequence.ShouldBe(entry.Sequence, context);
        var payload = row.Payload;
        payload.GetProperty("entryId").GetGuid().ShouldBe(entry.Id, context);
        payload.GetProperty("accountId").GetGuid().ShouldBe(accountId, context);
        payload.GetProperty("sequence").GetInt64().ShouldBe(entry.Sequence, context);
        payload.GetProperty("type").GetString().ShouldBe(entry.Type, context);
        payload.GetProperty("amount").GetDecimal().ShouldBe(entry.Amount, context);
        payload.GetProperty("balanceAfter").GetDecimal().ShouldBe(entry.BalanceAfter, context);
        payload.GetProperty("category").GetString().ShouldBe(entry.Category, context);
        payload.GetProperty("externalReference").GetString().ShouldBe(entry.ExternalReference, context);
        var reversed = payload.GetProperty("reversedEntryId");
        (reversed.ValueKind == JsonValueKind.Null ? (Guid?)null : reversed.GetGuid()).ShouldBe(entry.ReversedEntryId, context);
        payload.GetProperty("recordedAt").GetDateTimeOffset().UtcDateTime.ShouldBe(entry.RecordedAt.ToUniversalTime(), context);
    }
}
