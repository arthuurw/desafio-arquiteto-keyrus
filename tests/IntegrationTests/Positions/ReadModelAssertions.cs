using IntegrationTests.Ledger;
using MongoDB.Bson;

namespace IntegrationTests.Positions;

internal static class ReadModelAssertions
{
    // O documento em `entries` tem os valores do lançamento gravado no PostgreSQL, campo a campo.
    internal static void ShouldDescribe(this BsonDocument document, EntryRow entry, Guid accountId)
    {
        var context = document.ToString();
        document["_id"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard).ShouldBe(entry.Id, context);
        document["accountId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard).ShouldBe(accountId, context);
        document["sequence"].AsInt64.ShouldBe(entry.Sequence, context);
        document["type"].AsString.ShouldBe(entry.Type, context);
        document["amount"].AsDecimal.ShouldBe(entry.Amount, context);
        document["balanceAfter"].AsDecimal.ShouldBe(entry.BalanceAfter, context);
        document["category"].AsString.ShouldBe(entry.Category, context);
        document["externalReference"].AsString.ShouldBe(entry.ExternalReference, context);
        (document["reversedEntryId"].IsBsonNull ? (Guid?)null : document["reversedEntryId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard))
            .ShouldBe(entry.ReversedEntryId, context);
        document["recordedAtMicros"].AsInt64.ShouldBe(Micros(entry.RecordedAt), context);
    }

    // Microssegundos desde a época Unix, a precisão do PostgreSQL.
    internal static long Micros(DateTime utc) =>
        (DateTime.SpecifyKind(utc, DateTimeKind.Utc).Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;
}
