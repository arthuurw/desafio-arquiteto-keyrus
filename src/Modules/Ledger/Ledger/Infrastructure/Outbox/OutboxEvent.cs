using System.Text.Json;
using Ledger.Contracts;
using Ledger.Domain;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure.Outbox;

// Escrita e leitura do payload do outbox; o JSON é o contrato do `EntryRecorded.v1` (ADR-0035).
internal static class OutboxEvent
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static EntryRecorded ToEvent(Entry entry) => new(
        entry.Id,
        entry.AccountId,
        entry.Sequence,
        entry.Type == EntryType.Credit ? "credit" : "debit",
        entry.Amount.Amount,
        entry.BalanceAfter.Amount,
        entry.Category,
        entry.ExternalReference,
        entry.ReversedEntryId,
        entry.RecordedAt.ToUniversalTime());

    // Na transação do lançamento, depois de gravá-lo: o evento só existe se o lançamento existir (ADR-0006).
    // SQL direto, sem `RETURNING`: a Api só tem INSERT no outbox.
    internal static async Task AppendOutboxEventAsync(this LedgerDbContext db, Entry entry, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(ToEvent(entry), Json);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ledger.outbox (account_id, sequence, event_type, payload)
            VALUES ({entry.AccountId}, {entry.Sequence}, {EntryRecorded.EventType}, {payload}::jsonb)
            """,
            cancellationToken);
    }

    // Null quando o payload não é um `EntryRecorded.v1` completo.
    internal static EntryRecorded? Read(string eventType, string payload)
    {
        if (eventType != EntryRecorded.EventType)
        {
            return null;
        }

        try
        {
            var entry = JsonSerializer.Deserialize<EntryRecorded>(payload, Json);
            return entry is { Type: "credit" or "debit", Category: not null, ExternalReference: not null }
                && entry.EntryId != Guid.Empty && entry.AccountId != Guid.Empty && entry.Sequence > 0
                ? entry
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
