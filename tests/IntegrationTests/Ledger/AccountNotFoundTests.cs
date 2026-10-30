namespace IntegrationTests.Ledger;

[Collection(LedgerApiDefinition.Name)]
public sealed class AccountNotFoundTests(LedgerApi api)
{
    [Fact]
    public async Task UnknownAccountIsNotFound()
    {
        var unknown = new TestAccount(Guid.CreateVersion7(), Guid.NewGuid());
        var entriesBefore = await api.Rows.EntryCountAsync();

        using var credit = await api.Client.CreditAsync(unknown.Id, "10.00");
        using var debit = await api.Client.DebitAsync(unknown, "10.00");
        using var reversal = await api.Client.ReverseAsync(unknown.Id, Guid.CreateVersion7());
        using var limit = await api.Client.SetCreditLimitAsync(unknown.Id, "100.00");

        await credit.ShouldBeProblemAsync(404, "account-not-found");
        await debit.ShouldBeProblemAsync(404, "account-not-found");
        await reversal.ShouldBeProblemAsync(404, "account-not-found");
        await limit.ShouldBeProblemAsync(404, "account-not-found");
        (await api.Rows.EntryCountAsync()).ShouldBe(entriesBefore);
    }
}
