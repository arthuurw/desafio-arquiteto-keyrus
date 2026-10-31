using IntegrationTests.Infrastructure;
using Npgsql;

namespace IntegrationTests.Ledger;

// Outra transação com um INSERT ainda não confirmado: quem insere a mesma chave única espera por ela.
// Confirmada, a linha fica; descartada sem confirmar, desfaz.
internal sealed class HeldInsert : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private HeldInsert(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    internal static async Task<HeldInsert> StartAsync(PostgresDatabase database, string insert)
    {
        var connection = new NpgsqlConnection(database.ConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(insert, connection, transaction);
        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        return new HeldInsert(connection, transaction);
    }

    internal Task CommitAsync() => _transaction.CommitAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
