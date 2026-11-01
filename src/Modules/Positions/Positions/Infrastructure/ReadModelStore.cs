using Ledger.Contracts;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Positions.Infrastructure;

// Posição projetada de uma conta: até que versão o modelo de leitura chegou e o saldo depois dela.
internal sealed record Position(long Version, decimal Balance);

// Modelo de leitura no MongoDB (ADR-0006, ADR-0035): banco `positions`, um documento por conta em `positions` e um por
// lançamento em `entries`. O instante vai em microssegundos desde a época Unix: um `date` BSON guarda só milissegundos, e
// o saldo em um momento divergiria do PostgreSQL.
internal sealed class ReadModelStore
{
    internal const string DatabaseName = "positions";

    private readonly IMongoCollection<BsonDocument> _positions;
    private readonly IMongoCollection<BsonDocument> _entries;

    internal ReadModelStore(IMongoClient client)
    {
        var database = client.GetDatabase(DatabaseName);
        _positions = database.GetCollection<BsonDocument>("positions");
        _entries = database.GetCollection<BsonDocument>("entries");
    }

    // Timeout de conexão, seleção de servidor e socket iguais: nenhuma chamada fica parada além dele.
    internal static MongoClient CreateClient(string connectionString, TimeSpan timeout)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ConnectTimeout = timeout;
        settings.ServerSelectionTimeout = timeout;
        settings.SocketTimeout = timeout;
        return new MongoClient(settings);
    }

    // Conta sem documento ainda não tem nada projetado: versão 0, a de uma conta nova.
    internal async Task<Position> PositionAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var document = await _positions.Find(ById(accountId)).FirstOrDefaultAsync(cancellationToken);
        return document is null
            ? new Position(0, 0m)
            : new Position(document["version"].AsInt64, document["balance"].AsDecimal);
    }

    // O último lançamento até `atMicros`, entre os de `sequence <= maxSequence`.
    internal async Task<LedgerEntry?> LastEntryAtAsync(Guid accountId, long atMicros, long maxSequence, CancellationToken cancellationToken)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("accountId", Uuid(accountId)),
            Builders<BsonDocument>.Filter.Lte("recordedAtMicros", atMicros),
            Builders<BsonDocument>.Filter.Lte("sequence", maxSequence));
        var document = await _entries.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("recordedAtMicros").Descending("sequence"))
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ToEntry(document);
    }

    // Página em `sequence` decrescente; `[fromMicros, toMicros)` em `recordedAt`.
    internal async Task<IReadOnlyList<LedgerEntry>> EntryPageAsync(
        Guid accountId, long maxSequence, long? beforeSequence, long? fromMicros, long? toMicros, int limit, CancellationToken cancellationToken)
    {
        var builder = Builders<BsonDocument>.Filter;
        var filters = new List<FilterDefinition<BsonDocument>>
        {
            builder.Eq("accountId", Uuid(accountId)),
            builder.Lte("sequence", maxSequence),
        };
        if (beforeSequence is { } before)
        {
            filters.Add(builder.Lt("sequence", before));
        }

        if (fromMicros is { } from)
        {
            filters.Add(builder.Gte("recordedAtMicros", from));
        }

        if (toMicros is { } to)
        {
            filters.Add(builder.Lt("recordedAtMicros", to));
        }

        var documents = await _entries.Find(builder.And(filters))
            .Sort(Builders<BsonDocument>.Sort.Descending("sequence"))
            .Limit(limit)
            .ToListAsync(cancellationToken);
        return documents.Select(ToEntry).ToList();
    }

    // Os índices únicos são a barreira contra lançamento repetido; o segundo atende o saldo em um momento.
    internal Task EnsureIndexesAsync(CancellationToken cancellationToken) =>
        _entries.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("accountId").Ascending("sequence"),
                    new CreateIndexOptions { Unique = true, Name = "ux_entries_account_id_sequence" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("accountId").Ascending("recordedAtMicros").Ascending("sequence"),
                    new CreateIndexOptions { Unique = true, Name = "ux_entries_account_id_recorded_at_micros_sequence" }),
            ],
            cancellationToken);

    // Regravar o mesmo lançamento não muda nada: o documento é o mesmo a cada entrega.
    internal Task UpsertEntryAsync(EntryRecorded entry, CancellationToken cancellationToken) =>
        _entries.ReplaceOneAsync(
            ById(entry.EntryId),
            new BsonDocument
            {
                ["_id"] = Uuid(entry.EntryId),
                ["accountId"] = Uuid(entry.AccountId),
                ["sequence"] = new BsonInt64(entry.Sequence),
                ["type"] = entry.Type,
                ["amount"] = new BsonDecimal128(entry.Amount),
                ["balanceAfter"] = new BsonDecimal128(entry.BalanceAfter),
                ["category"] = entry.Category,
                ["externalReference"] = entry.ExternalReference,
                ["reversedEntryId"] = entry.ReversedEntryId is { } reversed ? Uuid(reversed) : BsonNull.Value,
                ["recordedAtMicros"] = new BsonInt64(Micros(entry.RecordedAt)),
            },
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);

    // Avança a posição só se ela ainda está em `from`; false quando outra entrega já a moveu.
    internal async Task<bool> TryAdvanceAsync(Guid accountId, long from, long to, decimal balance, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _positions.UpdateOneAsync(
                Builders<BsonDocument>.Filter.And(ById(accountId), Builders<BsonDocument>.Filter.Eq("version", new BsonInt64(from))),
                Builders<BsonDocument>.Update.Set("version", new BsonInt64(to)).Set("balance", new BsonDecimal128(balance)),
                new UpdateOptions { IsUpsert = true },
                cancellationToken);
            return result.MatchedCount == 1 || result.UpsertedId is not null;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // O documento existe em outra versão: o upsert tentou criar um segundo com o mesmo `_id`.
            return false;
        }
    }

    internal static long Micros(DateTimeOffset instant) => (instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMicrosecond;

    private static DateTimeOffset FromMicros(long micros) =>
        new(DateTimeOffset.UnixEpoch.UtcTicks + (micros * TimeSpan.TicksPerMicrosecond), TimeSpan.Zero);

    private static BsonBinaryData Uuid(Guid id) => new(id, GuidRepresentation.Standard);

    private static FilterDefinition<BsonDocument> ById(Guid id) => Builders<BsonDocument>.Filter.Eq("_id", Uuid(id));

    private static LedgerEntry ToEntry(BsonDocument document) => new(
        document["_id"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard),
        document["sequence"].AsInt64,
        document["type"].AsString,
        document["amount"].AsDecimal,
        document["balanceAfter"].AsDecimal,
        document["category"].AsString,
        document["externalReference"].AsString,
        document["reversedEntryId"].IsBsonNull ? null : document["reversedEntryId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard),
        FromMicros(document["recordedAtMicros"].AsInt64));
}
