using System.Text;
using System.Text.RegularExpressions;
using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;
using Npgsql;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed partial class DuplicateCpfTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task SecondRegistrationOfSameCpfIsRefused()
    {
        var ana = await environment.AnaAsync();
        var index = (await environment.Database.CustomerAsync(ana.Id)).ShouldNotBeNull().CpfBlindIndex;
        var deksBefore = await environment.Vault.DekIdsAsync();

        using var response = await environment.Client.RegisterAsync(
            new CustomerInput("Outra Pessoa", "52998224725", "1975-01-31", "outra@ledger.local"));

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        await response.ShouldBeProblemAsync(409, "cpf-already-registered");
        UuidPattern().IsMatch(text.Replace(ProblemTraceId(text), string.Empty, StringComparison.Ordinal)).ShouldBeFalse(text);
        (await environment.Database.CustomersWithIndexAsync(index)).ShouldBe(1);
        (await environment.Vault.DekIdsAsync()).Order().ShouldBe(deksBefore.Order());
    }

    [Fact]
    public async Task RefusalIsReplayedWithTheKey()
    {
        await environment.AnaAsync();
        var duplicate = new CustomerInput("Outra Pessoa", "529.982.247-25", "1975-01-31", "outra@ledger.local");
        var key = Guid.NewGuid().ToString();

        using var first = await environment.Client.RegisterAsync(duplicate, key);
        using var repeated = await environment.Client.RegisterAsync(duplicate, key);

        var firstBody = await first.BodyWithoutTraceAsync();
        await repeated.ShouldBeProblemAsync(409, "cpf-already-registered");
        first.IsReplay().ShouldBeFalse();
        repeated.IsReplay().ShouldBeTrue();
        System.Text.Json.Nodes.JsonNode.DeepEquals(await repeated.BodyWithoutTraceAsync(), firstBody).ShouldBeTrue();
        ((int)first.StatusCode).ShouldBe(409);
    }

    [Fact]
    public async Task UniqueIndexDecidesTheRace()
    {
        // Confirmada a transação que segurava o CPF, o cadastro que esperava no índice é recusado.
        var committed = CustomersClient.NewCustomer();
        var (status, type) = await RaceAsync(committed, commit: true);
        status.ShouldBe(409);
        type.ShouldBe("cpf-already-registered");

        // Desfeita, o cadastro que esperava segue.
        var rolledBack = CustomersClient.NewCustomer();
        (status, _) = await RaceAsync(rolledBack, commit: false);
        status.ShouldBe(201);
    }

    // Uma transação do teste insere, sem confirmar, uma linha com o blind index do CPF; o `POST` passa pela consulta
    // prévia, que não a vê, e para no índice único até o teste decidir.
    private async Task<(int Status, string? Type)> RaceAsync(CustomerInput customer, bool commit)
    {
        var (index, version) = await environment.Vault.HmacAsync("customers-blind-index", Encoding.UTF8.GetBytes(customer.Cpf));
        await using var holder = new NpgsqlConnection(environment.Database.ConnectionStringFor("postgres"));
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO customers.customers (id, name, cpf, birth_date, email, cpf_blind_index, cpf_blind_index_version)
            VALUES (gen_random_uuid(), '\x01', '\x01', '\x01', '\x01', @index, @version)
            """,
            holder,
            transaction))
        {
            insert.Parameters.AddWithValue("index", index);
            insert.Parameters.AddWithValue("version", version);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var registration = environment.Client.RegisterAsync(customer);
        await Eventually.WaitUntilAsync(
            async () =>
            {
                registration.IsCompleted.ShouldBeFalse("the registration did not wait for the held index");
                return await environment.Database.ScalarAsync<long>("postgres", """
                    SELECT count(DISTINCT l.pid)
                    FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
                    WHERE NOT l.granted AND a.usename = 'customers_app'
                    """) == 1;
            },
            TimeSpan.FromSeconds(10),
            () => "the registration never waited on the unique index");

        if (commit)
        {
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        using var response = await registration;
        var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        return ((int)response.StatusCode, body.TryGetProperty("type", out var type) ? type.GetString() : null);
    }

    private static string ProblemTraceId(string body) =>
        System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("traceId").GetString()!;

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex UuidPattern();
}
