using System.Buffers.Text;
using System.Text;
using IntegrationTests.Ledger;

namespace IntegrationTests.Positions;

// Extrato em `sequence` decrescente, paginado por cursor, igual pelos dois caminhos (ADR-0042, ADR-0007).
[Collection(PositionsApiDefinition.Name)]
public sealed class StatementTests(PositionsEnvironment environment)
{
    [Fact]
    public async Task PagesInDescendingSequence()
    {
        var account = await environment.AccountWithCreditsAsync(5);
        var empty = await environment.OpenAccountAsync();
        var entries = await environment.Rows.EntriesAsync(account.Id);

        var first = (await environment.Client.StatementAsync(account.Id, limit: "2")).ShouldBeOk();
        var second = (await environment.Client.StatementAsync(account.Id, limit: "2", cursor: Cursor(first))).ShouldBeOk();
        var third = (await environment.Client.StatementAsync(account.Id, limit: "2", cursor: Cursor(second))).ShouldBeOk();
        var none = (await environment.Client.StatementAsync(empty.Id)).ShouldBeOk();

        first["accountId"]!.GetValue<Guid>().ShouldBe(account.Id);
        PositionsClient.Sequences(first).ShouldBe([5, 4]);
        PositionsClient.Sequences(second).ShouldBe([3, 2]);
        PositionsClient.Sequences(third).ShouldBe([1]);
        Cursor(first).ShouldNotBeNull();
        Cursor(second).ShouldNotBeNull();
        third["nextCursor"].ShouldBeNull();
        third.AsObject().ContainsKey("nextCursor").ShouldBeTrue();
        none["items"]!.AsArray().ShouldBeEmpty();
        none["nextCursor"].ShouldBeNull();

        var item = first["items"]![0]!;
        var entry = entries[4];
        item.AsObject().Select(property => property.Key).ShouldBe(
            ["id", "type", "amount", "category", "externalReference", "reversedEntryId", "balanceAfter", "sequence", "recordedAt"]);
        item["id"]!.GetValue<Guid>().ShouldBe(entry.Id);
        item["type"]!.GetValue<string>().ShouldBe(entry.Type);
        item["amount"]!.GetValue<string>().ShouldBe("5.00");
        item["category"]!.GetValue<string>().ShouldBe(entry.Category);
        item["externalReference"]!.GetValue<string>().ShouldBe(entry.ExternalReference);
        item["reversedEntryId"].ShouldBeNull();
        item["balanceAfter"]!.GetValue<string>().ShouldBe("15.00");
        item["sequence"]!.GetValue<long>().ShouldBe(5);
        item["recordedAt"]!.GetValue<DateTime>().ShouldBe(entry.RecordedAt);
        item["recordedAt"]!.GetValue<string>().ShouldEndWith("Z");
    }

    [Fact]
    public async Task RangeIsHalfOpen()
    {
        var account = await environment.AccountWithCreditsAsync(5);
        var entries = await environment.Rows.EntriesAsync(account.Id);
        var from = PositionsClient.Iso(entries[1].RecordedAt);
        var to = PositionsClient.Iso(entries[3].RecordedAt);

        var both = (await environment.Client.StatementAsync(account.Id, from: from, to: to)).ShouldBeOk();
        var onlyFrom = (await environment.Client.StatementAsync(account.Id, from: from)).ShouldBeOk();
        var onlyTo = (await environment.Client.StatementAsync(account.Id, to: to)).ShouldBeOk();

        entries.Select(entry => entry.RecordedAt).Distinct().Count().ShouldBe(5);
        PositionsClient.Sequences(both).ShouldBe([3, 2]);
        PositionsClient.Sequences(onlyFrom).ShouldBe([5, 4, 3, 2]);
        PositionsClient.Sequences(onlyTo).ShouldBe([3, 2, 1]);
    }

    [Fact]
    public async Task PageSizeDefaultsAndLimits()
    {
        var account = await environment.AccountWithCreditsAsync(60);

        var standard = (await environment.Client.StatementAsync(account.Id)).ShouldBeOk();
        var one = (await environment.Client.StatementAsync(account.Id, limit: "1")).ShouldBeOk();
        var max = (await environment.Client.StatementAsync(account.Id, limit: "200")).ShouldBeOk();

        PositionsClient.Sequences(standard).Length.ShouldBe(50);
        PositionsClient.Sequences(one).ShouldBe([60]);
        PositionsClient.Sequences(max).Length.ShouldBe(60);
        max["nextCursor"].ShouldBeNull();
        foreach (var invalid in new[] { "0", "201", "abc" })
        {
            (await environment.Client.StatementAsync(account.Id, limit: invalid)).ShouldBeValidationErrorOn("limit");
        }
    }

    [Fact]
    public async Task InvalidRangeOrCursorIsRejected()
    {
        var account = await environment.AccountWithCreditsAsync(2);
        var forged = Base64Url.EncodeToString(Encoding.ASCII.GetBytes("not a cursor"));

        var reversed = await environment.Client.StatementAsync(account.Id, from: "2026-10-02T00:00:00Z", to: "2026-10-01T00:00:00Z");
        var equal = await environment.Client.StatementAsync(account.Id, from: "2026-10-01T00:00:00Z", to: "2026-10-01T00:00:00Z");
        var garbage = await environment.Client.StatementAsync(account.Id, cursor: "abc");
        var notCursor = await environment.Client.StatementAsync(account.Id, cursor: forged);

        reversed.Status.ShouldBe(400, reversed.Text);
        reversed.Body["type"]!.GetValue<string>().ShouldBe("validation-error");
        var empty = equal.ShouldBeOk();
        empty["items"]!.AsArray().ShouldBeEmpty();
        empty["nextCursor"].ShouldBeNull();
        garbage.ShouldBeValidationErrorOn("cursor");
        notCursor.ShouldBeValidationErrorOn("cursor");
    }

    [Fact]
    public async Task NewEntriesDoNotShiftPages()
    {
        var account = await environment.AccountWithCreditsAsync(5);

        var sequences = new List<long>();
        var page = (await environment.Client.StatementAsync(account.Id, limit: "2")).ShouldBeOk();
        sequences.AddRange(PositionsClient.Sequences(page));
        for (var i = 0; i < 3; i++)
        {
            await environment.Client.CreditOkAsync(account.Id, "1.00");
        }

        while (Cursor(page) is { } cursor)
        {
            page = (await environment.Client.StatementAsync(account.Id, limit: "2", cursor: cursor)).ShouldBeOk();
            sequences.AddRange(PositionsClient.Sequences(page));
        }

        sequences.ShouldBe([5, 4, 3, 2, 1]);
    }

    [Fact]
    public async Task BothPathsReturnSameStatement()
    {
        var account = await environment.AccountWithCreditsAsync(3);
        await environment.ProjectAsync(account.Id);

        var mark = environment.ApiLog.Mark();
        var fromMongo = await environment.Client.StatementPagesAsync(account.Id);
        var paths = environment.ReadPaths(mark);
        var fromLedger = await environment.FallbackClient.StatementPagesAsync(account.Id);

        // Worker desligado: o crédito novo não está no MongoDB e aparece mesmo assim.
        await environment.Client.CreditOkAsync(account.Id, "9.00");
        var unprojected = (await environment.Client.StatementAsync(account.Id, limit: "2")).ShouldBeOk();

        List<QueryResponse> whilePaused;
        await using (await environment.Mongo.PauseAsync())
        {
            whilePaused = await environment.Client.StatementPagesAsync(account.Id);
        }

        await environment.WarmUpAsync();

        fromMongo.Count.ShouldBe(2);
        paths.ShouldAllBe(path => path == PositionsLog.ServedFromReadModel);
        paths.Count.ShouldBe(2);
        fromLedger.ShouldBe(fromMongo);
        PositionsClient.Sequences(unprojected).ShouldBe([4, 3]);
        unprojected["items"]![0]!["amount"]!.GetValue<string>().ShouldBe("9.00");
        whilePaused.Select(response => PositionsClient.Sequences(response.ShouldBeOk())).SelectMany(page => page).ShouldBe([4, 3, 2, 1]);
    }

    private static string? Cursor(System.Text.Json.Nodes.JsonNode page) => page["nextCursor"]?.GetValue<string>();
}
