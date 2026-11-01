using System.Text;
using Npgsql;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class CustomerStorageTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task RowHoldsOnlyCiphertext()
    {
        var ana = await environment.AnaAsync();
        var row = (await environment.Database.CustomerAsync(ana.Id)).ShouldNotBeNull();
        var plaintexts = new Dictionary<string, string>
        {
            ["name"] = "Ana Maria Souza",
            ["cpf"] = "52998224725",
            ["birth_date"] = "1990-04-15",
            ["email"] = "ana.souza@ledger.local",
        };
        string[] forbidden = ["Ana Maria Souza", "52998224725", "529.982.247-25", "1990-04-15", "ana.souza@ledger.local"];

        foreach (var (field, value) in row.Fields)
        {
            value[0].ShouldBe((byte)0x01, field);
            value.Length.ShouldBe(1 + 12 + Encoding.UTF8.GetByteCount(plaintexts[field]) + 16, field);
            foreach (var plain in forbidden)
            {
                value.AsSpan().IndexOf(Encoding.UTF8.GetBytes(plain)).ShouldBe(-1, $"{field} contains '{plain}'");
            }
        }
    }

    [Fact]
    public async Task DekLivesOnlyInTheVault()
    {
        var ana = await environment.AnaAsync();
        var dek = (await environment.Vault.DekAsync(ana.Id)).ShouldNotBeNull();

        dek.Length.ShouldBe(32);
        (await ColumnsContainingAsync(dek)).ShouldBeEmpty();
    }

    [Fact]
    public async Task EachCustomerHasItsOwnKey()
    {
        var first = CustomersClient.NewCustomer();
        var second = first with { Cpf = CustomersClient.NewCpf() };

        var firstId = await environment.Client.RegisteredAsync(first);
        var secondId = await environment.Client.RegisteredAsync(second);

        var firstDek = (await environment.Vault.DekAsync(firstId)).ShouldNotBeNull();
        var secondDek = (await environment.Vault.DekAsync(secondId)).ShouldNotBeNull();
        firstDek.ShouldNotBe(secondDek);
        var firstName = (await environment.Database.CustomerAsync(firstId)).ShouldNotBeNull().Name;
        var secondName = (await environment.Database.CustomerAsync(secondId)).ShouldNotBeNull().Name;
        firstName.ShouldNotBe(secondName);
    }

    // Toda coluna `bytea` dos schemas da aplicação em que a sequência aparece, inteira ou dentro do valor.
    private async Task<List<string>> ColumnsContainingAsync(byte[] needle)
    {
        await using var connection = new NpgsqlConnection(environment.Database.ConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var columns = new List<(string Table, string Column, string Type)>();
        await using (var list = new NpgsqlCommand(
            """
            SELECT quote_ident(table_schema) || '.' || quote_ident(table_name), column_name, data_type
            FROM information_schema.columns
            WHERE table_schema IN ('customers', 'ledger')
            """,
            connection))
        await using (var reader = await list.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        columns.ShouldContain(column => column.Table == "customers.customers" && column.Column == "cpf");
        var hits = new List<string>();
        foreach (var (table, column, type) in columns)
        {
            // Texto também: a DEK em base64 ou hex numa coluna de texto conta como vazada.
            var sql = type == "bytea"
                ? $"SELECT count(*) FROM {table} WHERE position(@needle IN \"{column}\") > 0"
                : $"SELECT count(*) FROM {table} WHERE position(@base64 IN \"{column}\"::text) > 0 OR position(@hex IN \"{column}\"::text) > 0";
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("needle", needle);
            command.Parameters.AddWithValue("base64", Convert.ToBase64String(needle));
            command.Parameters.AddWithValue("hex", Convert.ToHexStringLower(needle));
            if ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))! > 0)
            {
                hits.Add($"{table}.{column}");
            }
        }

        return hits;
    }
}
