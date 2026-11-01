using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;
using MongoDB.Bson;
using MongoDB.Driver;

namespace IntegrationTests.Positions;

[Collection(PositionsProjectionDefinition.Name)]
public sealed class MongoSchemaTests(PositionsEnvironment environment)
{
    [Fact]
    public async Task ProjectionStoresDocumentedShape()
    {
        var account = await environment.Client.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "12.34");
        await environment.ProjectAsync(account.Id);

        var indexes = await (await environment.Mongo.Entries.Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);
        var unique = indexes
            .Where(index => index.TryGetValue("unique", out var flag) && flag.AsBoolean)
            .Select(index => index["key"].AsBsonDocument.ToString())
            .Order()
            .ToList();
        var position = (await environment.Mongo.PositionAsync(account.Id)).ShouldNotBeNull();
        var entry = (await environment.Mongo.EntriesAsync(account.Id)).ShouldHaveSingleItem();

        unique.ShouldBe(["{ \"accountId\" : 1, \"recordedAtMicros\" : 1, \"sequence\" : 1 }", "{ \"accountId\" : 1, \"sequence\" : 1 }"]);
        position["_id"].AsBsonBinaryData.SubType.ShouldBe(BsonBinarySubType.UuidStandard);
        position["version"].BsonType.ShouldBe(BsonType.Int64);
        position["balance"].BsonType.ShouldBe(BsonType.Decimal128);
        entry["amount"].BsonType.ShouldBe(BsonType.Decimal128);
        entry["balanceAfter"].BsonType.ShouldBe(BsonType.Decimal128);
        entry["recordedAtMicros"].BsonType.ShouldBe(BsonType.Int64);

        // O usuário da Api só lê: gravar e criar índice são recusados (código 13, Unauthorized).
        using var reader = new MongoClient(environment.Mongo.ConnectionStringFor(MongoReadModel.Reader));
        var readerPositions = reader.GetDatabase("positions").GetCollection<BsonDocument>("positions");
        var write = await Should.ThrowAsync<MongoCommandException>(() =>
            readerPositions.InsertOneAsync(new BsonDocument("_id", Guid.NewGuid().ToString()), cancellationToken: TestContext.Current.CancellationToken));
        var index = await Should.ThrowAsync<MongoCommandException>(() =>
            readerPositions.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("balance")),
                cancellationToken: TestContext.Current.CancellationToken));
        var read = await readerPositions.Find(new BsonDocument("_id", MongoReadModel.Uuid(account.Id))).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        write.Code.ShouldBe(13);
        index.Code.ShouldBe(13);
        read.ShouldNotBeNull();
    }
}
