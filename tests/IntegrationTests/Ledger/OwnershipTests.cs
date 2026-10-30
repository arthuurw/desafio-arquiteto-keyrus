using System.Diagnostics;
using IntegrationTests.Identity;

namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class OwnershipTests(LedgerApi api)
{
    private static readonly Guid Ana = Guid.Parse(TestCustomers.Ana);
    private static readonly Guid Bruno = Guid.Parse(TestCustomers.Bruno);

    [Fact]
    public async Task DelegatedCallerReachesOnlyOwnAccounts()
    {
        var anaAccount = await api.Client.OpenAccountAsync(Ana);
        var brunoAccount = await api.Client.OpenAccountAsync(Bruno);
        await api.Client.CreditOkAsync(anaAccount.Id, "100.00");
        await api.Client.CreditOkAsync(brunoAccount.Id, "100.00");
        var brunoBefore = await api.Rows.AccountAsync(brunoAccount.Id);

        // O token é sempre o da Ana; só a conta da rota muda.
        using var own = await api.Client.DebitAsync(anaAccount, "10.00");
        using var others = await api.Client.DebitAsync(brunoAccount with { CustomerId = Ana }, "10.00");
        using var unknown = await api.Client.DebitAsync(new TestAccount(Guid.CreateVersion7(), Ana), "10.00");

        await own.ShouldBeEntryAsync();
        var othersProblem = await others.ShouldBeProblemAsync(404, "account-not-found");
        var unknownProblem = await unknown.ShouldBeProblemAsync(404, "account-not-found");
        othersProblem.GetProperty("title").GetString().ShouldBe(unknownProblem.GetProperty("title").GetString());
        othersProblem.GetProperty("detail").GetString().ShouldBe(unknownProblem.GetProperty("detail").GetString());
        (await api.Rows.AccountAsync(brunoAccount.Id)).ShouldBe(brunoBefore);
        (await api.Rows.EntriesAsync(brunoAccount.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ForeignLockedAccountIsNotFound()
    {
        var brunoAccount = await api.Client.OpenAccountAsync(Bruno);
        await api.Client.CreditOkAsync(brunoAccount.Id, "100.00");
        var brunoBefore = await api.Rows.AccountAsync(brunoAccount.Id);
        using var unknown = await api.Client.DebitAsync(new TestAccount(Guid.CreateVersion7(), Ana), "10.00");
        var unknownProblem = await unknown.ShouldBeProblemAsync(404, "account-not-found");

        // Com a conta do Bruno ocupada, a Ana não pode distinguir "existe e está travada" de "não existe".
        HttpResponseMessage locked;
        Stopwatch clock;
        await using (await AccountLockHolder.HoldAsync(api.Database, brunoAccount.Id))
        {
            clock = Stopwatch.StartNew();
            locked = await api.Client.DebitAsync(brunoAccount with { CustomerId = Ana }, "10.00");
            clock.Stop();
        }

        using (locked)
        {
            var lockedProblem = await locked.ShouldBeProblemAsync(404, "account-not-found");
            lockedProblem.GetProperty("title").GetString().ShouldBe(unknownProblem.GetProperty("title").GetString());
            lockedProblem.GetProperty("detail").GetString().ShouldBe(unknownProblem.GetProperty("detail").GetString());
        }

        // Respondeu sem entrar na fila do lock, cujo limite é 2 s.
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1.5));
        (await api.Rows.AccountAsync(brunoAccount.Id)).ShouldBe(brunoBefore);
        (await api.Rows.EntriesAsync(brunoAccount.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ServiceCallerReachesAnyAccount()
    {
        var anaAccount = await api.Client.OpenAccountAsync(Ana);
        var brunoAccount = await api.Client.OpenAccountAsync(Bruno);

        using var toAna = await api.Client.CreditAsync(anaAccount.Id, "10.00");
        using var toBruno = await api.Client.CreditAsync(brunoAccount.Id, "10.00");

        await toAna.ShouldBeEntryAsync();
        await toBruno.ShouldBeEntryAsync();
    }
}
