using IntegrationTests.Infrastructure;
using Npgsql;

namespace IntegrationTests.Customers;

// Linha de `customers.customers` como o banco a guarda, lida pelo superusuário.
internal sealed record StoredCustomer(
    Guid Id, byte[] Name, byte[] Cpf, byte[] BirthDate, byte[] Email, byte[] CpfBlindIndex, int CpfBlindIndexVersion, DateTimeOffset RegisteredAt)
{
    internal IReadOnlyDictionary<string, byte[]> Fields => new Dictionary<string, byte[]>
    {
        ["name"] = Name,
        ["cpf"] = Cpf,
        ["birth_date"] = BirthDate,
        ["email"] = Email,
    };
}

// O estado observável de Customers no PostgreSQL.
internal static class CustomerStore
{
    internal static async Task<StoredCustomer?> CustomerAsync(this PostgresDatabase database, Guid id)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT id, name, cpf, birth_date, email, cpf_blind_index, cpf_blind_index_version, registered_at
            FROM customers.customers WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        return new StoredCustomer(
            reader.GetGuid(0),
            (byte[])reader[1],
            (byte[])reader[2],
            (byte[])reader[3],
            (byte[])reader[4],
            (byte[])reader[5],
            reader.GetInt32(6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    internal static async Task<long> CustomersWithIndexAsync(this PostgresDatabase database, byte[] blindIndex)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM customers.customers WHERE cpf_blind_index = @index", connection);
        command.Parameters.AddWithValue("index", blindIndex);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    internal static Task<long> CustomerCountAsync(this PostgresDatabase database) =>
        CountAsync(database, "SELECT count(*) FROM customers.customers");

    internal static Task<long> KeyCountAsync(this PostgresDatabase database) =>
        CountAsync(database, "SELECT count(*) FROM customers.idempotency_keys");

    internal static async Task<byte[]?> RequestHashAsync(this PostgresDatabase database, string clientId, Guid key)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT request_hash FROM customers.idempotency_keys WHERE client_id = @client AND idempotency_key = @key", connection);
        command.Parameters.AddWithValue("client", clientId);
        command.Parameters.AddWithValue("key", key);
        return (byte[]?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    // Grava um campo cifrado direto no banco, como um acesso indevido faria.
    internal static async Task OverwriteFieldAsync(this PostgresDatabase database, Guid id, string column, byte[] value)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"UPDATE customers.customers SET {column} = @value WHERE id = @id", connection);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("id", id);
        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    private static async Task<long> CountAsync(PostgresDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
