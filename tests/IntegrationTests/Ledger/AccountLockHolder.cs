using IntegrationTests.Infrastructure;
using Npgsql;

namespace IntegrationTests.Ledger;

// Outra transação segurando `FOR UPDATE` na conta até ser descartada, quando desfaz e libera.
internal sealed class AccountLockHolder : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private AccountLockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    internal static async Task<AccountLockHolder> HoldAsync(PostgresDatabase database, Guid accountId)
    {
        var connection = new NpgsqlConnection(database.ConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1 FROM ledger.accounts WHERE id = @id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", accountId);
        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        return new AccountLockHolder(connection, transaction);
    }

    public async ValueTask DisposeAsync()
    {
        await _transaction.RollbackAsync(TestContext.Current.CancellationToken);
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
