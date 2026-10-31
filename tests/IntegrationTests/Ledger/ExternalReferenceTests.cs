using IntegrationTests.Identity;
using Npgsql;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class ExternalReferenceTests(LedgerApi api)
{
    [Fact]
    public async Task DuplicateReferenceIsRejected()
    {
        var (account, _) = await api.Client.FundedAccountAsync();
        var (otherAccount, _) = await api.Client.FundedAccountAsync();
        using var original = await api.Client.CreditAsync(account.Id, "10.00", externalReference: "E2E-DUP");
        var originalId = (await original.ShouldBeEntryAsync()).GetProperty("id").GetGuid();
        using var firstReversal = await api.Client.ReverseAsync(account.Id, originalId, externalReference: "E2E-REV");
        await firstReversal.ShouldBeEntryAsync();
        var otherCreditId = (await api.Client.CreditOkAsync(account.Id, "5.00")).GetProperty("id").GetGuid();
        var entriesBefore = await api.Rows.EntryCountAsync();

        using var sameAccount = await api.Client.CreditAsync(account.Id, "10.00", externalReference: "E2E-DUP");
        using var sameClientOtherAccount = await api.Client.CreditAsync(otherAccount.Id, "10.00", externalReference: "E2E-DUP");
        using var secondReversal = await api.Client.ReverseAsync(account.Id, otherCreditId, externalReference: "E2E-REV");
        var entriesAfterDuplicates = await api.Rows.EntryCountAsync();
        using var otherClient = await api.Client.DebitAsync(account, "1.00", externalReference: "E2E-DUP");

        await sameAccount.ShouldBeProblemAsync(409, "duplicate-external-reference");
        await sameClientOtherAccount.ShouldBeProblemAsync(409, "duplicate-external-reference");
        await secondReversal.ShouldBeProblemAsync(409, "duplicate-external-reference");
        entriesAfterDuplicates.ShouldBe(entriesBefore);
        await otherClient.ShouldBeEntryAsync();
    }

    [Fact]
    public async Task ConflictNamesVisibleEntry()
    {
        var (account, _) = await api.Client.FundedAccountAsync();
        var creditReference = LedgerClient.NewReference();
        var debitReference = LedgerClient.NewReference();
        var creditId = (await (await api.Client.CreditAsync(account.Id, "10.00", externalReference: creditReference)).ShouldBeEntryAsync())
            .GetProperty("id").GetGuid();
        var debitId = (await (await api.Client.DebitAsync(account, "10.00", externalReference: debitReference)).ShouldBeEntryAsync())
            .GetProperty("id").GetGuid();

        using var service = await api.Client.CreditAsync(account.Id, "10.00", externalReference: creditReference);
        using var owner = await api.Client.DebitAsync(account, "10.00", externalReference: debitReference);

        var serviceProblem = await service.ShouldBeProblemAsync(409, "duplicate-external-reference");
        var ownerProblem = await owner.ShouldBeProblemAsync(409, "duplicate-external-reference");
        serviceProblem.GetProperty("entryId").GetGuid().ShouldBe(creditId);
        serviceProblem.GetProperty("accountId").GetGuid().ShouldBe(account.Id);
        ownerProblem.GetProperty("entryId").GetGuid().ShouldBe(debitId);
        ownerProblem.GetProperty("accountId").GetGuid().ShouldBe(account.Id);
    }

    [Fact]
    public async Task ConflictHidesOtherCustomersEntry()
    {
        var (ana, _) = await api.Client.FundedAccountAsync();
        var (bruno, _) = await api.Client.FundedAccountAsync();
        var reference = LedgerClient.NewReference();
        var anaEntryId = (await (await api.Client.DebitAsync(ana, "10.00", externalReference: reference)).ShouldBeEntryAsync())
            .GetProperty("id").GetGuid();

        using var response = await api.Client.DebitAsync(bruno, "10.00", externalReference: reference);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var problem = await response.ShouldBeProblemAsync(409, "duplicate-external-reference");
        problem.TryGetProperty("entryId", out _).ShouldBeFalse();
        problem.TryGetProperty("accountId", out _).ShouldBeFalse();
        text.ShouldNotContain(anaEntryId.ToString());
        text.ShouldNotContain(ana.Id.ToString());
    }

    [Fact]
    public async Task AccountIsCheckedBeforeReference()
    {
        var (ana, _) = await api.Client.FundedAccountAsync();
        var creditReference = LedgerClient.NewReference();
        var debitReference = LedgerClient.NewReference();
        using (var credit = await api.Client.CreditAsync(ana.Id, "10.00", externalReference: creditReference))
        {
            await credit.ShouldBeEntryAsync();
        }

        using (var debit = await api.Client.DebitAsync(ana, "10.00", externalReference: debitReference))
        {
            await debit.ShouldBeEntryAsync();
        }

        var entriesBefore = await api.Rows.EntryCountAsync();

        // Referência repetida, mas a conta não existe, ou é de outro cliente: responde como qualquer escrita nela.
        using var missingAccount = await api.Client.CreditAsync(Guid.NewGuid(), "10.00", externalReference: creditReference);
        using var otherCustomer = await api.Client.SendJsonAsync(
            HttpMethod.Post,
            $"/v1/accounts/{ana.Id}/debits",
            new { amount = "10.00", category = "PIX_OUT", externalReference = debitReference },
            LedgerTokens.Channel(Guid.NewGuid()));

        await missingAccount.ShouldBeProblemAsync(404, "account-not-found");
        await otherCustomer.ShouldBeProblemAsync(404, "account-not-found");
        (await api.Rows.EntryCountAsync()).ShouldBe(entriesBefore);
    }

    [Fact]
    public async Task DatabaseRejectsDuplicateReference()
    {
        var first = await api.Client.OpenAccountAsync();
        var second = await api.Client.OpenAccountAsync();
        await api.Database.ExecuteAsync("postgres", Entry(first.Id, "DB-DUP", "db-test", Guid.NewGuid()));

        var exception = await Should.ThrowAsync<PostgresException>(() => api.Database.ExecuteAsync("postgres", Entry(second.Id, "DB-DUP", "db-test", Guid.NewGuid())));

        exception.SqlState.ShouldBe("23505");
        exception.ConstraintName.ShouldBe("ux_entries_client_id_external_reference");
    }

    [Fact]
    public async Task ConcurrentDuplicateReferenceRecordsOne()
    {
        var (first, _) = await api.Client.FundedAccountAsync();
        var (second, _) = await api.Client.FundedAccountAsync();
        var reference = LedgerClient.NewReference();
        TestAccount[] accounts = [first, second];

        // Chaves diferentes e contas diferentes: nem a chave nem o lock da conta separam as duas.
        var results = await StartGate.RunTogetherAsync(2, async index =>
        {
            using var response = await api.Client.CreditAsync(accounts[index].Id, "10.00", externalReference: reference);
            var body = await response.JsonAsync();
            return (Status: (int)response.StatusCode, Type: body.TryGetProperty("type", out var type) ? type.GetString() : null);
        });

        results.Select(result => result.Status).Order().ShouldBe([201, 409]);
        results.Single(result => result.Status == 409).Type.ShouldBe("duplicate-external-reference");
        (await api.Rows.EntryCountByReferenceAsync(reference)).ShouldBe(1);
    }

    [Fact]
    public async Task UniqueIndexRaceIsStoredAsConflict()
    {
        var (account, _) = await api.Client.FundedAccountAsync();
        var other = await api.Client.OpenAccountAsync();
        var reference = LedgerClient.NewReference();
        var key = Guid.NewGuid().ToString();
        var heldEntryId = Guid.NewGuid();
        Task<HttpResponseMessage> credit;

        // Outra transação já inseriu a mesma referência e não confirmou: a consulta prévia da Api não a vê,
        // e o INSERT da Api espera no índice único até ela confirmar.
        await using (var held = await HeldWrite.StartAsync(api.Database, Entry(other.Id, reference, "payments-hub", heldEntryId)))
        {
            credit = api.Client.CreditAsync(account.Id, "10.00", externalReference: reference, idempotencyKey: key);
            await api.Database.WaitForBlockedRequestsAsync(1);
            await held.CommitAsync();
        }

        using var raced = await credit;
        using var repeated = await api.Client.CreditAsync(account.Id, "10.00", externalReference: reference, idempotencyKey: key);

        var problem = await raced.ShouldBeProblemAsync(409, "duplicate-external-reference");
        problem.GetProperty("entryId").GetGuid().ShouldBe(heldEntryId);
        problem.GetProperty("accountId").GetGuid().ShouldBe(other.Id);
        (await api.Rows.EntryCountByReferenceAsync(reference)).ShouldBe(1);
        (await api.Rows.EntriesAsync(account.Id)).Count.ShouldBe(1);
        // A recusa da corrida ficou guardada com a chave: a transação seguiu depois do savepoint.
        await repeated.ShouldBeProblemAsync(409, "duplicate-external-reference");
        repeated.IsReplay().ShouldBeTrue();
    }

    // Primeiro lançamento de uma conta nova, gravado fora da Api.
    private static string Entry(Guid account, string reference, string clientId, Guid id) => $"""
        INSERT INTO ledger.entries (id, account_id, sequence, type, amount, balance_after, recorded_at, category, external_reference, client_id)
        VALUES ('{id}', '{account}', 1, 'credit', 1, 1, now(), 'PIX_IN', '{reference}', '{clientId}')
        """;
}
