using IntegrationTests.Infrastructure;
using Npgsql;

namespace IntegrationTests.Ledger;

internal sealed record AccountRow(Guid CustomerId, decimal Balance, decimal CreditLimit, long Version, DateTime? LastRecordedAt);

internal sealed record EntryRow(
    Guid Id,
    long Sequence,
    string Type,
    decimal Amount,
    decimal BalanceAfter,
    DateTime RecordedAt,
    string Category,
    string ExternalReference,
    string ClientId,
    Guid? ReversedEntryId);

// Leitura direta das tabelas do Ledger como superusuário: o efeito observável de cada escrita.
internal sealed class LedgerRows(PostgresDatabase database)
{
    internal async Task<AccountRow?> AccountAsync(Guid accountId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT customer_id, balance, credit_limit, version, last_recorded_at FROM ledger.accounts WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", accountId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        return new AccountRow(
            reader.GetGuid(0),
            reader.GetDecimal(1),
            reader.GetDecimal(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4));
    }

    internal async Task<List<EntryRow>> EntriesAsync(Guid accountId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT id, sequence, type, amount, balance_after, recorded_at, category, external_reference, client_id, reversed_entry_id
            FROM ledger.entries WHERE account_id = @id ORDER BY sequence
            """,
            connection);
        command.Parameters.AddWithValue("id", accountId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var entries = new List<EntryRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            entries.Add(new EntryRow(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDateTime(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9)));
        }

        return entries;
    }

    internal Task<long> AccountCountAsync() => CountAsync("SELECT count(*) FROM ledger.accounts");

    internal Task<long> EntryCountAsync() => CountAsync("SELECT count(*) FROM ledger.entries");

    // Invariantes do ADR-0003 e do ADR-0005 sobre o que está gravado.
    internal async Task ShouldKeepInvariantsAsync(Guid accountId)
    {
        var account = (await AccountAsync(accountId)).ShouldNotBeNull();
        var entries = await EntriesAsync(accountId);

        var balance = 0m;
        DateTime? previousRecordedAt = null;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            balance += entry.Type == "credit" ? entry.Amount : -entry.Amount;
            entry.BalanceAfter.ShouldBe(balance, $"balance_after of sequence {entry.Sequence}");
            entry.Sequence.ShouldBe(i + 1);
            if (previousRecordedAt is not null)
            {
                entry.RecordedAt.ShouldBeGreaterThanOrEqualTo(previousRecordedAt.Value, $"recorded_at of sequence {entry.Sequence}");
            }

            previousRecordedAt = entry.RecordedAt;
        }

        entries.ShouldNotBeEmpty();
        account.Balance.ShouldBe(entries[^1].BalanceAfter);
        account.Version.ShouldBe(entries[^1].Sequence);
        account.LastRecordedAt.ShouldBe(entries[^1].RecordedAt);
        (account.Balance >= -account.CreditLimit).ShouldBeTrue();
    }

    private async Task<long> CountAsync(string sql) => await database.ScalarAsync<long>("postgres", sql);

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(database.ConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }
}
