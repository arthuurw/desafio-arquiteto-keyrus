using Npgsql;
using Testcontainers.PostgreSql;

namespace IntegrationTests.Infrastructure;

// PostgreSQL real, inicializado pelo mesmo script de roles e schemas do docker-compose.
internal sealed class PostgresDatabase : IAsyncDisposable
{
    internal const string Image = "postgres:18.6-alpine";
    private const string DatabaseName = "ledger_db";

    // Senhas exclusivas do ambiente local e dos testes; as das roles vêm do script de init.
    private static readonly Dictionary<string, string> Passwords = new()
    {
        ["postgres"] = "postgres_dev",
        ["migrator"] = "migrator_dev",
        ["ledger_app"] = "ledger_app_dev",
        ["customers_app"] = "customers_app_dev",
    };

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithDatabase(DatabaseName)
        .WithUsername("postgres")
        .WithPassword(Passwords["postgres"])
        .WithResourceMapping(new FileInfo(Repository.InitScript), "/docker-entrypoint-initdb.d/")
        .Build();

    internal static async Task<PostgresDatabase> StartAsync()
    {
        var database = new PostgresDatabase();
        await database._container.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    internal string ConnectionStringFor(string role) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = _container.Hostname,
            Port = _container.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort),
            Database = DatabaseName,
            Username = role,
            Password = Passwords[role],
            Pooling = false,
        }.ConnectionString;

    internal async Task ExecuteAsync(string role, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionStringFor(role));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    internal async Task<T?> ScalarAsync<T>(string role, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionStringFor(role));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? default : (T)value;
    }

    internal async Task<List<string>> ColumnAsync(string role, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionStringFor(role));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    // Executa como a role e devolve o SQLSTATE do PostgreSQL, ou null quando o comando é aceito.
    internal async Task<string?> SqlStateOfAsync(string role, string sql)
    {
        try
        {
            await ExecuteAsync(role, sql);
            return null;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
    }

    // Tabela nova criada pelo migrator, com uma linha, para provar privilégios sobre objetos do schema.
    internal async Task<string> CreateMigratorTableAsync(string schema)
    {
        var table = $"{schema}.probe_{Guid.NewGuid():N}";
        await ExecuteAsync("migrator", $"CREATE TABLE {table} (id int PRIMARY KEY, value text); INSERT INTO {table} VALUES (1, 'a');");
        return table;
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
