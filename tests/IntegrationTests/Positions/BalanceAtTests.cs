using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;

namespace IntegrationTests.Positions;

// Saldo em um momento: `balanceAfter` do último lançamento com `recordedAt <= at`, igual pelos dois caminhos (ADR-0003).
[Collection(PositionsApiDefinition.Name)]
public sealed class BalanceAtTests(PositionsEnvironment environment)
{
    [Fact]
    public async Task BalanceAtInstantIsLastEntryUpToIt()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "100.00");
        await environment.Client.DebitOkAsync(account, "40.00");
        await environment.Client.CreditOkAsync(account.Id, "5.00");
        await environment.ProjectAsync(account.Id);
        var entries = await environment.Rows.EntriesAsync(account.Id);
        var second = entries[1].RecordedAt;

        (string At, string Balance, long Version)[] cases =
        [
            (PositionsClient.Iso(entries[0].RecordedAt.AddSeconds(-1)), "0.00", 0),
            (PositionsClient.Iso(second), "60.00", 2),
            (PositionsClient.Iso(second.AddTicks(-TimeSpan.TicksPerMicrosecond)), "100.00", 1),
            (PositionsClient.Iso(entries[2].RecordedAt.AddMilliseconds(1)), "65.00", 3),
        ];

        foreach (var (at, balance, version) in cases)
        {
            var mark = environment.ApiLog.Mark();
            var fromMongo = await environment.Client.BalanceAsync(account.Id, at);
            var fromLedger = await environment.FallbackClient.BalanceAsync(account.Id, at);

            var body = fromMongo.ShouldBeOk();
            body["balance"]!.GetValue<string>().ShouldBe(balance, at);
            body["version"]!.GetValue<long>().ShouldBe(version, at);
            var echoed = body["at"]!.GetValue<string>();
            echoed.ShouldEndWith("Z", Case.Sensitive, at);
            Instant(echoed).ShouldBe(Instant(at), at);
            fromLedger.ShouldBe(fromMongo, at);
            environment.ReadPaths(mark).ShouldBe([PositionsLog.ServedFromReadModel], at);
        }
    }

    [Fact]
    public async Task MicrosecondPrecisionOnBothPaths()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "10.00");
        // O próximo lançamento é gravado no instante `last_recorded_at` (o relógio nunca volta, ADR-0005): um instante
        // no futuro próximo terminando em .123456 fixa o `recordedAt` dele.
        var target = await environment.Database.ScalarAsync<DateTime>("postgres", $"""
            UPDATE ledger.accounts
            SET last_recorded_at = date_trunc('second', clock_timestamp()) + interval '2 seconds 123456 microseconds'
            WHERE id = '{account.Id}'
            RETURNING last_recorded_at
            """);
        await environment.Client.CreditOkAsync(account.Id, "5.00");
        (await environment.Rows.EntriesAsync(account.Id))[1].RecordedAt.ShouldBe(target);
        await Eventually.WaitUntilAsync(
            async () => await environment.Database.ScalarAsync<bool>("postgres", $"SELECT clock_timestamp() > '{PositionsClient.Iso(target)}'::timestamptz"),
            TimeSpan.FromSeconds(10),
            () => "database clock never passed the entry",
            TimeSpan.FromMilliseconds(100));
        await environment.ProjectAsync(account.Id);
        var before = PositionsClient.Iso(target).Replace(".123456Z", ".123400Z", StringComparison.Ordinal);
        var exact = PositionsClient.Iso(target);

        var mark = environment.ApiLog.Mark();
        var beforeFromMongo = await environment.Client.BalanceAsync(account.Id, before);
        var exactFromMongo = await environment.Client.BalanceAsync(account.Id, exact);
        var paths = environment.ReadPaths(mark);
        var beforeFromLedger = await environment.FallbackClient.BalanceAsync(account.Id, before);
        var exactFromLedger = await environment.FallbackClient.BalanceAsync(account.Id, exact);

        exact.ShouldEndWith(".123456Z");
        beforeFromMongo.ShouldBeOk()["balance"]!.GetValue<string>().ShouldBe("10.00");
        beforeFromMongo.Body["version"]!.GetValue<long>().ShouldBe(1);
        exactFromMongo.ShouldBeOk()["balance"]!.GetValue<string>().ShouldBe("15.00");
        exactFromMongo.Body["version"]!.GetValue<long>().ShouldBe(2);
        beforeFromLedger.ShouldBe(beforeFromMongo);
        exactFromLedger.ShouldBe(exactFromMongo);
        paths.ShouldBe([PositionsLog.ServedFromReadModel, PositionsLog.ServedFromReadModel]);
    }

    [Fact]
    public async Task FutureInstantIsRejected()
    {
        var account = await environment.OpenAccountAsync();
        var now = await environment.Database.ScalarAsync<DateTime>("postgres", "SELECT clock_timestamp()");

        var future = await environment.Client.BalanceAsync(account.Id, PositionsClient.Iso(now.AddMinutes(1)));
        var past = await environment.Client.BalanceAsync(account.Id, PositionsClient.Iso(now.AddSeconds(-1)));

        future.ShouldBeValidationErrorOn("at");
        past.ShouldBeOk()["balance"]!.GetValue<string>().ShouldBe("0.00");
    }

    private static DateTime Instant(string iso) =>
        DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
}
