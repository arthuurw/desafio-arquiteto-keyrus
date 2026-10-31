using IntegrationTests.Infrastructure;
using Npgsql;

namespace IntegrationTests.Ledger;

// Outra transação com uma escrita de uma linha ainda não confirmada: quem disputa a mesma chave única espera
// por ela. Confirmada, a escrita vale; descartada sem confirmar, desfaz.
internal sealed class HeldWrite : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private HeldWrite(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    internal static async Task<HeldWrite> StartAsync(PostgresDatabase database, string write)
    {
        var connection = new NpgsqlConnection(database.ConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(write, connection, transaction);
        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        return new HeldWrite(connection, transaction);
    }

    internal Task CommitAsync() => _transaction.CommitAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
