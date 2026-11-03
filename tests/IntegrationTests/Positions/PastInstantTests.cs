using System.Diagnostics;
using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;

namespace IntegrationTests.Positions;

// Um instante passado responde sempre igual (ADR-0002, ADR-0045): a leitura com `at` ou `to` espera a escrita em
// voo na conta.
[Collection(PositionsApiDefinition.Name)]
public sealed class PastInstantTests(PositionsEnvironment environment)
{
    [Fact]
    public async Task BalanceAtWaitsForTheWriteInFlight()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "5.00");
        await using var gate = await CommitGate.CloseAsync(environment.Database, account.Id);
        var credit = environment.Client.CreditAsync(account.Id, "10.00");
        await gate.WaitForWriterAsync();
        var at = await DatabaseNowAsync();

        var reading = environment.Client.BalanceAsync(account.Id, at);
        await WaitForBlockedReaderAsync();
        await gate.OpenAsync();
        using (var created = await credit)
        {
            ((int)created.StatusCode).ShouldBe(201);
        }

        var first = await reading;
        first.ShouldBeOk()["balance"]!.GetValue<string>().ShouldBe("15.00", first.Text);
        first.Body["version"]!.GetValue<long>().ShouldBe(2);
        (await environment.Client.BalanceAsync(account.Id, at)).Text.ShouldBe(first.Text);
    }

    [Fact]
    public async Task BalanceAtWithoutTheUndoneWrite()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "5.00");
        await using var gate = await CommitGate.CloseAsync(environment.Database, account.Id);
        var credit = environment.Client.CreditAsync(account.Id, "10.00");
        await gate.WaitForWriterAsync();
        var at = await DatabaseNowAsync();

        var reading = environment.Client.BalanceAsync(account.Id, at);
        await WaitForBlockedReaderAsync();
        await gate.CancelWriterAsync();
        using (var refused = await credit)
        {
            ((int)refused.StatusCode).ShouldNotBe(201);
        }

        var first = await reading;
        first.ShouldBeOk()["balance"]!.GetValue<string>().ShouldBe("5.00", first.Text);
        first.Body["version"]!.GetValue<long>().ShouldBe(1);
        (await environment.Rows.EntriesAsync(account.Id)).Count.ShouldBe(1);
        (await environment.Client.BalanceAsync(account.Id, at)).Text.ShouldBe(first.Text);
    }

    [Fact]
    public async Task StatementToWaitsForTheWriteInFlight()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "5.00");
        await using var gate = await CommitGate.CloseAsync(environment.Database, account.Id);
        var credit = environment.Client.CreditAsync(account.Id, "10.00");
        await gate.WaitForWriterAsync();
        var to = await DatabaseNowAsync();

        var reading = environment.Client.StatementAsync(account.Id, to: to);
        await WaitForBlockedReaderAsync();
        await gate.OpenAsync();
        using (var created = await credit)
        {
            ((int)created.StatusCode).ShouldBe(201);
        }

        var first = await reading;
        PositionsClient.Sequences(first.ShouldBeOk()).ShouldBe([2, 1], first.Text);
        first.Body["items"]![0]!["amount"]!.GetValue<string>().ShouldBe("10.00");
        (await environment.Client.StatementAsync(account.Id, to: to)).Text.ShouldBe(first.Text);
    }

    [Fact]
    public async Task PastInstantReadTimesOutWith409()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "5.00");
        var instant = await DatabaseNowAsync();
        await using var holder = await AccountLockHolder.HoldAsync(environment.Database, account.Id);

        var balanceClock = Stopwatch.StartNew();
        var balance = await environment.Client.BalanceAsync(account.Id, instant);
        var balanceWait = balanceClock.Elapsed;
        var statementClock = Stopwatch.StartNew();
        var statement = await environment.Client.StatementAsync(account.Id, to: instant);
        var statementWait = statementClock.Elapsed;

        foreach (var (response, wait) in new[] { (balance, balanceWait), (statement, statementWait) })
        {
            response.Status.ShouldBe(409, response.Text);
            response.Body["type"]!.GetValue<string>().ShouldBe("concurrency-timeout", response.Text);
            wait.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task CurrentReadsDoNotWaitForTheLock()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "5.00");
        await using var holder = await AccountLockHolder.HoldAsync(environment.Database, account.Id);

        var balance = await environment.Client.BalanceAsync(account.Id);
        var statement = await environment.Client.StatementAsync(account.Id);

        balance.ShouldBeOk()["balance"]!.GetValue<string>().ShouldBe("5.00", balance.Text);
        PositionsClient.Sequences(statement.ShouldBeOk()).ShouldBe([1], statement.Text);
    }

    [Fact]
    public async Task OtherAccountLockDoesNotBlock()
    {
        var account = await environment.OpenAccountAsync();
        var other = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "5.00");
        var instant = await DatabaseNowAsync();
        await using var holder = await AccountLockHolder.HoldAsync(environment.Database, other.Id);

        var balance = await environment.Client.BalanceAsync(account.Id, instant);
        var statement = await environment.Client.StatementAsync(account.Id, to: instant);

        balance.ShouldBeOk()["balance"]!.GetValue<string>().ShouldBe("5.00", balance.Text);
        PositionsClient.Sequences(statement.ShouldBeOk()).ShouldBe([1], statement.Text);
    }

    [Fact]
    public async Task WriteAfterReadIsStampedLater()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "5.00");
        var at = await DatabaseNowAsync();
        var before = await environment.Client.BalanceAsync(account.Id, at);
        before.ShouldBeOk();

        await environment.Client.CreditOkAsync(account.Id, "10.00");

        var entries = await environment.Rows.EntriesAsync(account.Id);
        entries[^1].RecordedAt.ShouldBeGreaterThan(DateTime.Parse(at, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        (await environment.Client.BalanceAsync(account.Id, at)).Text.ShouldBe(before.Text);
    }

    // O prazo de 3 s por comando vale só para as escritas (ADR-0054): a leitura de um instante passado, que abre
    // transação para esperar a escrita em voo, passa dele sem ser cortada.
    [Fact]
    public async Task PastInstantReadIsNotCutByTheWriteCommandTimeout()
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "5.00");
        var at = await DatabaseNowAsync();

        QueryResponse reading;
        await using (await SlowAccountReadAsync(account.Id, seconds: 4))
        {
            reading = await environment.Client.BalanceAsync(account.Id, at);
        }

        reading.ShouldBeOk()["balance"]!.GetValue<string>().ShouldBe("5.00", reading.Text);
    }

    // Política de linha que segura por `seconds` toda consulta da Api à conta dada; as outras contas passam direto. O
    // superusuário a cria e a remove: a role da Api não ignora RLS, a dona da tabela sim.
    private async Task<IAsyncDisposable> SlowAccountReadAsync(Guid accountId, int seconds)
    {
        var policy = $"test_slow_{Guid.NewGuid():N}";
        await environment.Database.ExecuteAsync("postgres", $"""
            CREATE POLICY {policy} ON ledger.accounts USING (id <> '{accountId}' OR (SELECT true FROM pg_sleep({seconds})));
            ALTER TABLE ledger.accounts ENABLE ROW LEVEL SECURITY;
            """);
        return new AsyncCleanup(() => environment.Database.ExecuteAsync("postgres", $"""
            ALTER TABLE ledger.accounts DISABLE ROW LEVEL SECURITY;
            DROP POLICY {policy} ON ledger.accounts;
            """));
    }

    // O relógio do banco, o mesmo que carimba `recorded_at`.
    private async Task<string> DatabaseNowAsync() =>
        PositionsClient.Iso(await environment.Database.ScalarAsync<DateTime>("postgres", "SELECT clock_timestamp()"));

    // A leitura parada no lock da conta; o advisory lock é o da escrita parada no COMMIT, não o da leitura.
    private Task WaitForBlockedReaderAsync() => environment.Database.WaitForBlockedRequestsAsync(1, exceptAdvisory: true);
}
