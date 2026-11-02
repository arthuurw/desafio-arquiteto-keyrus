using Npgsql;
using Testcontainers.PostgreSql;

namespace IntegrationTests.Infrastructure;

// PostgreSQL real, inicializado pelo mesmo script de roles e schemas do docker-compose, e com o mesmo log de DDL. Um
// servidor só atende a execução inteira: cada instância é um banco próprio, copiado do banco que o script preparou, e
// some no descarte. Um container por fixture somava dezenas deles na suíte completa.
internal sealed class PostgresDatabase : IAsyncDisposable
{
    internal const string Image = "postgres:18.6-alpine@sha256:10a5c69a83035ab5239f5620d708520e8b28f5aa1f2b20143e6024532edadfb5";

    // O banco que o script de init prepara: molde de cada banco de teste, sem conexão aberta, como a cópia exige.
    private const string TemplateName = "ledger_db";

    // Senhas exclusivas do ambiente local e dos testes; as das roles vêm do script de init.
    private static readonly Dictionary<string, string> Passwords = new()
    {
        ["postgres"] = "postgres_dev",
        ["migrator"] = "migrator_dev",
        ["ledger_app"] = "ledger_app_dev",
        ["customers_app"] = "customers_app_dev",
        ["ledger_worker"] = "ledger_worker_dev",
        ["customers_worker"] = "customers_worker_dev",
    };

    private static readonly Lazy<Task<PostgreSqlContainer>> Server = new(StartServerAsync);

    // Duas cópias do mesmo molde ao mesmo tempo disputam o banco de origem: uma de cada vez.
    private static readonly SemaphoreSlim Copying = new(1, 1);

    private readonly PostgreSqlContainer _container;

    private PostgresDatabase(PostgreSqlContainer container, string name)
    {
        _container = container;
        Name = name;
    }

    internal string Name { get; }

    internal static async Task<PostgresDatabase> StartAsync()
    {
        var container = await Server.Value;
        var database = new PostgresDatabase(container, $"ledger_test_{Guid.NewGuid():N}");
        await Copying.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await database.ExecuteOnServerAsync($"CREATE DATABASE {database.Name} TEMPLATE {TemplateName}");
        }
        finally
        {
            Copying.Release();
        }

        // A cópia não leva os privilégios do banco: o mesmo acesso que o script de init dá ao molde.
        await database.ExecuteOnServerAsync($"""
            REVOKE ALL ON DATABASE {database.Name} FROM PUBLIC;
            GRANT CONNECT ON DATABASE {database.Name} TO migrator, ledger_app, customers_app, ledger_worker, customers_worker;
            """);
        return database;
    }

    // `max_connections` acima do padrão de 100: as Apis e os Workers de várias fixtures abrem pools no mesmo servidor.
    private static async Task<PostgreSqlContainer> StartServerAsync()
    {
        var container = new PostgreSqlBuilder(Image)
            .WithDatabase(TemplateName)
            .WithUsername("postgres")
            .WithPassword(Passwords["postgres"])
            .WithResourceMapping(new FileInfo(Repository.InitScript), "/docker-entrypoint-initdb.d/")
            .WithCommand("-c", "log_statement=ddl", "-c", "log_line_prefix=%m [%p] %u@%d ", "-c", "max_connections=400")
            .Build();
        await container.StartAsync(CancellationToken.None);
        return container;
    }

    // Sem pool por padrão; a Api sob teste usa pool, como em produção.
    internal string ConnectionStringFor(string role, bool pooling = false) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = _container.Hostname,
            Port = _container.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort),
            Database = Name,
            Username = role,
            Password = Passwords[role],
            Pooling = pooling,
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

    // O log do servidor até agora, de todos os bancos dele: o PostgreSQL escreve no stderr do container.
    internal async Task<string> ServerLogAsync()
    {
        var (stdout, stderr) = await _container.GetLogsAsync(ct: TestContext.Current.CancellationToken);
        return stdout + stderr;
    }

    // O servidor continua para as outras fixtures; `FORCE` derruba as conexões que os pools ainda seguram.
    public async ValueTask DisposeAsync() => await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS {Name} WITH (FORCE)");

    // Comandos de servidor, como criar e remover bancos, rodam fora do banco de teste e do molde.
    private async Task ExecuteOnServerAsync(string sql)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionStringFor("postgres")) { Database = "postgres" }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
