using System.Diagnostics;
using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;
using MongoDB.Bson;
using MongoDB.Driver;

namespace IntegrationTests.Positions;

[Collection(PositionsApiDefinition.Name)]
public sealed class BalanceTests(PositionsEnvironment environment)
{
    [Fact]
    public async Task CurrentBalanceReflectsAccount()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "100.00");
        await environment.Client.DebitOkAsync(account, "30.25");
        var empty = await environment.OpenAccountAsync();
        var negative = await environment.OpenAccountAsync();
        await environment.Client.SetCreditLimitOkAsync(negative.Id, "50.00");
        await environment.Client.DebitOkAsync(negative, "10.00");

        var before = await DatabaseNowAsync();
        var balance = (await environment.Client.BalanceAsync(account.Id)).ShouldBeOk();
        var after = await DatabaseNowAsync();
        var emptyBalance = (await environment.Client.BalanceAsync(empty.Id)).ShouldBeOk();
        var negativeBalance = (await environment.Client.BalanceAsync(negative.Id)).ShouldBeOk();

        balance["accountId"]!.GetValue<Guid>().ShouldBe(account.Id);
        balance["balance"]!.GetValue<string>().ShouldBe("69.75");
        balance["version"]!.GetValue<long>().ShouldBe(2);
        var at = balance["at"]!.GetValue<string>();
        at.ShouldEndWith("Z");
        DateTime.Parse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
            .ShouldBeInRange(before, after);
        emptyBalance["balance"]!.GetValue<string>().ShouldBe("0.00");
        emptyBalance["version"]!.GetValue<long>().ShouldBe(0);
        negativeBalance["balance"]!.GetValue<string>().ShouldBe("-10.00");
        negativeBalance["version"]!.GetValue<long>().ShouldBe(1);
    }

    [Fact]
    public async Task UnprojectedEntryIsIncluded()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "5.00");
        await environment.ProjectAsync(account.Id);

        // Worker desligado: o crédito seguinte não chega ao MongoDB.
        await environment.Client.CreditOkAsync(account.Id, "10.00");
        var mark = environment.ApiLog.Mark();
        var balance = (await environment.Client.BalanceAsync(account.Id)).ShouldBeOk();

        (await environment.Mongo.PositionAsync(account.Id)).ShouldNotBeNull()["version"].AsInt64.ShouldBe(1);
        balance["balance"]!.GetValue<string>().ShouldBe("15.00");
        balance["version"]!.GetValue<long>().ShouldBe(2);
        environment.ReadPaths(mark).ShouldContain(PositionsLog.Behind);
    }

    [Fact]
    public async Task ProjectedBalanceIsServedFromMongo()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "40.00");
        await environment.ProjectAsync(account.Id);
        var filter = new BsonDocument("_id", MongoReadModel.Uuid(account.Id));

        // Só o MongoDB tem 1.00: a resposta com 1.00 prova que veio dele.
        await environment.Mongo.Positions.UpdateOneAsync(
            filter, new BsonDocument("$set", new BsonDocument("balance", new BsonDecimal128(1.00m))), cancellationToken: TestContext.Current.CancellationToken);
        var fromMongo = (await environment.Client.BalanceAsync(account.Id)).ShouldBeOk();

        await environment.Mongo.Positions.UpdateOneAsync(
            filter, new BsonDocument("$set", new BsonDocument("version", 7L)), cancellationToken: TestContext.Current.CancellationToken);
        var fromLedger = (await environment.Client.BalanceAsync(account.Id)).ShouldBeOk();

        fromMongo["balance"]!.GetValue<string>().ShouldBe("1.00");
        fromMongo["version"]!.GetValue<long>().ShouldBe(1);
        fromLedger["balance"]!.GetValue<string>().ShouldBe("40.00");
        fromLedger["version"]!.GetValue<long>().ShouldBe(1);
    }

    [Fact]
    public async Task MongoDownFallsBackToPostgres()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "20.00");
        await environment.Client.DebitOkAsync(account, "5.50");
        await environment.ProjectAsync(account.Id);

        QueryResponse paused;
        TimeSpan elapsed;
        int mark;
        await using (await environment.Mongo.PauseAsync())
        {
            mark = environment.ApiLog.Mark();
            var clock = Stopwatch.StartNew();
            paused = await environment.Client.BalanceAsync(account.Id);
            elapsed = clock.Elapsed;
        }

        var unreachable = await environment.FallbackClient.BalanceAsync(account.Id);
        await environment.WarmUpAsync();

        var pausedBody = paused.ShouldBeOk();
        pausedBody["balance"]!.GetValue<string>().ShouldBe("14.50");
        pausedBody["version"]!.GetValue<long>().ShouldBe(2);
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        environment.ReadPaths(mark).ShouldContain(message => message.StartsWith(PositionsLog.TimedOutPrefix, StringComparison.Ordinal));
        var unreachableBody = unreachable.ShouldBeOk();
        unreachableBody["balance"]!.GetValue<string>().ShouldBe("14.50");
        unreachableBody["version"]!.GetValue<long>().ShouldBe(2);
    }

    private Task<DateTime> DatabaseNowAsync() =>
        environment.Database.ScalarAsync<DateTime>("postgres", "SELECT clock_timestamp()");
}
