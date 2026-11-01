using System.Globalization;
using System.Text.Json;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class CreditTests(LedgerApi api)
{
    [Fact]
    public async Task CreditReturnsEntryAndUpdatesAccount()
    {
        var account = await api.OpenAccountAsync();

        using var response = await api.Client.CreditAsync(account.Id, "150.75", "PIX_IN", "E2E-1");
        var entry = await response.ShouldBeEntryAsync();
        var row = await api.Rows.AccountAsync(account.Id);
        var stored = (await api.Rows.EntriesAsync(account.Id)).ShouldHaveSingleItem();

        var id = entry.GetProperty("id").GetGuid();
        id.Version.ShouldBe(7);
        entry.GetProperty("accountId").GetGuid().ShouldBe(account.Id);
        entry.GetProperty("type").GetString().ShouldBe("credit");
        entry.GetProperty("amount").GetString().ShouldBe("150.75");
        entry.GetProperty("category").GetString().ShouldBe("PIX_IN");
        entry.GetProperty("externalReference").GetString().ShouldBe("E2E-1");
        entry.GetProperty("reversedEntryId").ValueKind.ShouldBe(JsonValueKind.Null);
        entry.GetProperty("balanceAfter").GetString().ShouldBe("150.75");
        entry.GetProperty("sequence").GetInt64().ShouldBe(1);
        entry.GetProperty("version").GetInt64().ShouldBe(1);
        var recordedAt = entry.GetProperty("recordedAt").GetString().ShouldNotBeNull();
        recordedAt.ShouldEndWith("Z");
        var recordedAtUtc = DateTime.Parse(recordedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        row.ShouldNotBeNull().Balance.ShouldBe(150.75m);
        row.Version.ShouldBe(1);
        stored.Id.ShouldBe(id);
        stored.Sequence.ShouldBe(1);
        stored.Type.ShouldBe("credit");
        stored.Amount.ShouldBe(150.75m);
        stored.BalanceAfter.ShouldBe(150.75m);
        stored.Category.ShouldBe("PIX_IN");
        stored.ExternalReference.ShouldBe("E2E-1");
        stored.ClientId.ShouldBe("payments-hub");
        stored.ReversedEntryId.ShouldBeNull();
        stored.RecordedAt.ShouldBe(recordedAtUtc);
    }

    [Fact]
    public async Task CreditIsAcceptedBelowZero()
    {
        var account = await api.OpenAccountAsync();
        await api.Client.SetCreditLimitOkAsync(account.Id, "500.00");
        await api.Client.DebitOkAsync(account, "500.00");

        using var response = await api.Client.CreditAsync(account.Id, "0.01");

        (await response.ShouldBeEntryAsync()).GetProperty("balanceAfter").GetString().ShouldBe("-499.99");
    }
}
