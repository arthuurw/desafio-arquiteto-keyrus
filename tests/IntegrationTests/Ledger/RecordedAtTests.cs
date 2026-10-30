using System.Globalization;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class RecordedAtTests(LedgerApi api)
{
    [Fact]
    public async Task RecordedAtNeverGoesBackwards()
    {
        var account = await api.Client.OpenAccountAsync();
        await api.Client.CreditOkAsync(account.Id, "10.00");
        // Simula um relógio que voltou: o último registro ficou 1 h à frente do relógio do banco.
        await api.Database.ExecuteAsync("postgres",
            $"UPDATE ledger.accounts SET last_recorded_at = date_trunc('milliseconds', clock_timestamp()) + interval '1 hour' WHERE id = '{account.Id}'");
        var ahead = (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().LastRecordedAt.ShouldNotBeNull();

        using var response = await api.Client.CreditAsync(account.Id, "1.00");
        var recordedAt = (await response.ShouldBeEntryAsync()).GetProperty("recordedAt").GetString().ShouldNotBeNull();

        DateTime.Parse(recordedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ShouldBe(ahead);
        (await api.Rows.EntriesAsync(account.Id))[^1].RecordedAt.ShouldBe(ahead);
        (await api.Rows.AccountAsync(account.Id)).ShouldNotBeNull().LastRecordedAt.ShouldBe(ahead);
        ahead.ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(30));
    }
}
