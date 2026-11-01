using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;
using MongoDB.Bson;
using MongoDB.Driver;

namespace IntegrationTests.Positions;

// Bordas do extrato e do saldo em um momento que os dois caminhos precisam tratar igual (ADR-0007, ADR-0035).
[Collection(PositionsApiDefinition.Name)]
public sealed class StatementBoundaryTests(PositionsEnvironment environment)
{
    [Fact]
    public async Task SubMicrosecondInstantsKeepTheirMeaning()
    {
        var account = await environment.AccountWithCreditsAsync(3);
        var entries = await environment.Rows.EntriesAsync(account.Id);
        // 0,5 µs depois do lançamento 2: `recordedAt < to` inclui o 2; o mesmo instante como `from` exclui o 2.
        var halfAfterSecond = PositionsClient.Iso(entries[1].RecordedAt).Replace("Z", "5Z", StringComparison.Ordinal);
        var later = "2026-10-01T00:00:00.0000007Z";
        var earlier = "2026-10-01T00:00:00.0000001Z";

        var to = (await environment.Client.StatementAsync(account.Id, to: halfAfterSecond)).ShouldBeOk();
        var from = (await environment.Client.StatementAsync(account.Id, from: halfAfterSecond)).ShouldBeOk();
        var reversed = await environment.Client.StatementAsync(account.Id, from: later, to: earlier);
        var at = (await environment.Client.BalanceAsync(account.Id, halfAfterSecond)).ShouldBeOk();

        PositionsClient.Sequences(to).ShouldBe([2, 1]);
        PositionsClient.Sequences(from).ShouldBe([3]);
        reversed.Status.ShouldBe(400, reversed.Text);
        reversed.Body["type"]!.GetValue<string>().ShouldBe("validation-error");
        at["version"]!.GetValue<long>().ShouldBe(2);
        at["at"]!.GetValue<string>().ShouldBe(halfAfterSecond);
    }

    [Fact]
    public async Task RangeIsHalfOpenOnBothPaths()
    {
        var account = await environment.AccountWithCreditsAsync(5);
        await environment.ProjectAsync(account.Id);
        var entries = await environment.Rows.EntriesAsync(account.Id);
        var from = PositionsClient.Iso(entries[1].RecordedAt);
        var to = PositionsClient.Iso(entries[3].RecordedAt);

        var mark = environment.ApiLog.Mark();
        var fromMongo = await environment.Client.StatementAsync(account.Id, from: from, to: to);
        var paths = environment.ReadPaths(mark);
        var fromLedger = await environment.FallbackClient.StatementAsync(account.Id, from: from, to: to);

        PositionsClient.Sequences(fromMongo.ShouldBeOk()).ShouldBe([3, 2]);
        fromLedger.ShouldBe(fromMongo);
        paths.ShouldBe([PositionsLog.ServedFromReadModel]);
    }

    [Fact]
    public async Task LastPageHasNoCursorOnBothPaths()
    {
        var account = await environment.AccountWithCreditsAsync(4);
        await environment.ProjectAsync(account.Id);

        var mark = environment.ApiLog.Mark();
        var fromMongo = await environment.Client.StatementPagesAsync(account.Id);
        var paths = environment.ReadPaths(mark);
        var fromLedger = await environment.FallbackClient.StatementPagesAsync(account.Id);

        // Restam exatamente `limit` itens na segunda página: ela é a última.
        fromMongo.Count.ShouldBe(2);
        PositionsClient.Sequences(fromMongo[0].ShouldBeOk()).ShouldBe([4, 3]);
        fromMongo[0].Body["nextCursor"].ShouldNotBeNull();
        PositionsClient.Sequences(fromMongo[1].ShouldBeOk()).ShouldBe([2, 1]);
        fromMongo[1].Body["nextCursor"].ShouldBeNull();
        fromLedger.ShouldBe(fromMongo);
        paths.ShouldBe([PositionsLog.ServedFromReadModel, PositionsLog.ServedFromReadModel]);
    }

    [Fact]
    public async Task ReadModelAnswersOnlyUpToTheVersion()
    {
        var account = await environment.AccountWithCreditsAsync(2);
        await environment.ProjectAsync(account.Id);
        var documents = await environment.Mongo.EntriesAsync(account.Id);
        // Lançamento 3 gravado no MongoDB e a versão ainda em 2: o estado entre os dois passos da projeção.
        var third = documents[1].DeepClone().AsBsonDocument;
        third["_id"] = MongoReadModel.Uuid(Guid.NewGuid());
        third["sequence"] = 3L;
        third["recordedAtMicros"] = third["recordedAtMicros"].AsInt64 + 1;
        third["balanceAfter"] = new BsonDecimal128(999.00m);
        await environment.Mongo.Entries.InsertOneAsync(third, cancellationToken: TestContext.Current.CancellationToken);
        var now = PositionsClient.Iso(await environment.Database.ScalarAsync<DateTime>("postgres", "SELECT clock_timestamp()"));

        var mark = environment.ApiLog.Mark();
        var statement = await environment.Client.StatementAsync(account.Id);
        var balanceAt = await environment.Client.BalanceAsync(account.Id, now);
        var paths = environment.ReadPaths(mark);
        var statementFromLedger = await environment.FallbackClient.StatementAsync(account.Id);
        var balanceAtFromLedger = await environment.FallbackClient.BalanceAsync(account.Id, now);

        PositionsClient.Sequences(statement.ShouldBeOk()).ShouldBe([2, 1]);
        balanceAt.ShouldBeOk()["version"]!.GetValue<long>().ShouldBe(2);
        balanceAt.Body["balance"]!.GetValue<string>().ShouldBe("3.00");
        statementFromLedger.ShouldBe(statement);
        balanceAtFromLedger.ShouldBe(balanceAt);
        paths.ShouldBe([PositionsLog.ServedFromReadModel, PositionsLog.ServedFromReadModel]);
    }
}
