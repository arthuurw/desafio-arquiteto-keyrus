using IntegrationTests.Infrastructure;

namespace IntegrationTests.Ledger;

[Collection(MigratedDatabaseDefinition.Name)]
public sealed class LedgerTablesTests(MigratedDatabase fixture)
{
    private const string InsufficientPrivilege = "42501";
    private const string CheckViolation = "23514";
    private const string UniqueViolation = "23505";

    private readonly PostgresDatabase _database = fixture.Database;

    [Fact]
    public async Task LedgerAppPrivilegesOnLedgerTables()
    {
        var entries = await PrivilegesAsync("ledger.entries");
        var accounts = await PrivilegesAsync("ledger.accounts");

        var updateEntries = await _database.SqlStateOfAsync("ledger_app", "UPDATE ledger.entries SET category = 'FEE'");
        var deleteEntries = await _database.SqlStateOfAsync("ledger_app", "DELETE FROM ledger.entries");
        var deleteAccounts = await _database.SqlStateOfAsync("ledger_app", "DELETE FROM ledger.accounts");
        var truncateEntries = await _database.SqlStateOfAsync("ledger_app", "TRUNCATE ledger.entries");
        var truncateAccounts = await _database.SqlStateOfAsync("ledger_app", "TRUNCATE ledger.accounts");

        entries.ShouldBe(["INSERT", "SELECT"]);
        // UPDATE em `accounts` só por coluna, sem privilégio de tabela.
        accounts.ShouldBe(["INSERT", "SELECT"]);
        updateEntries.ShouldBe(InsufficientPrivilege);
        deleteEntries.ShouldBe(InsufficientPrivilege);
        deleteAccounts.ShouldBe(InsufficientPrivilege);
        truncateEntries.ShouldBe(InsufficientPrivilege);
        truncateAccounts.ShouldBe(InsufficientPrivilege);
    }

    [Fact]
    public async Task DatabaseRejectsBrokenInvariants()
    {
        var account = Guid.NewGuid();
        var credit = Guid.NewGuid();
        await _database.ExecuteAsync("ledger_app",
            $"INSERT INTO ledger.accounts (id, customer_id, created_at) VALUES ('{account}', '{Guid.NewGuid()}', now())");
        await _database.ExecuteAsync("ledger_app", Entry(credit, account, sequence: 1, "credit", "10", reversedEntryId: null));

        var balanceBelowLimit = await _database.SqlStateOfAsync("ledger_app",
            $"UPDATE ledger.accounts SET balance = -0.0001 WHERE id = '{account}'");
        var zeroAmount = await _database.SqlStateOfAsync("ledger_app", Entry(Guid.NewGuid(), account, sequence: 2, "credit", "0", null));
        var reversalType = await _database.SqlStateOfAsync("ledger_app", Entry(Guid.NewGuid(), account, sequence: 2, "reversal", "10", null));
        var repeatedSequence = await _database.SqlStateOfAsync("ledger_app", Entry(Guid.NewGuid(), account, sequence: 1, "debit", "10", null));
        await _database.ExecuteAsync("ledger_app", Entry(Guid.NewGuid(), account, sequence: 2, "debit", "10", credit));
        var secondReversal = await _database.SqlStateOfAsync("ledger_app", Entry(Guid.NewGuid(), account, sequence: 3, "debit", "10", credit));

        balanceBelowLimit.ShouldBe(CheckViolation);
        zeroAmount.ShouldBe(CheckViolation);
        reversalType.ShouldBe(CheckViolation);
        repeatedSequence.ShouldBe(UniqueViolation);
        secondReversal.ShouldBe(UniqueViolation);
    }

    private Task<List<string>> PrivilegesAsync(string table) => _database.ColumnAsync("postgres", $"""
        SELECT privilege
        FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privilege
        WHERE has_table_privilege('ledger_app', '{table}', privilege)
        ORDER BY privilege
        """);

    private static string Entry(Guid id, Guid account, long sequence, string type, string amount, Guid? reversedEntryId) => $"""
        INSERT INTO ledger.entries (id, account_id, sequence, type, amount, balance_after, recorded_at, category, external_reference, client_id, reversed_entry_id)
        VALUES ('{id}', '{account}', {sequence}, '{type}', {amount}, 0, now(), 'PIX_IN', 'R-{id:N}', 'test', {(reversedEntryId is null ? "NULL" : $"'{reversedEntryId}'")})
        """;
}
