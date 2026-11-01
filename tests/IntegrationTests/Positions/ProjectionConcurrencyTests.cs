using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;

namespace IntegrationTests.Positions;

[Collection(PositionsProjectionDefinition.Name)]
public sealed class ProjectionConcurrencyTests(PositionsEnvironment environment)
{
    private const int Accounts = 5;
    private const int CreditsPerAccount = 40;

    [Fact]
    public async Task TwoWorkersProjectEachEntryOnce()
    {
        var accounts = new List<TestAccount>();
        for (var i = 0; i < Accounts; i++)
        {
            accounts.Add(await environment.Client.OpenAccountAsync());
        }

        using var first = environment.StartWorker();
        using var second = environment.StartWorker();
        await first.WaitForOutputAsync(output => output.Contains("Outbox relay polling", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
        await second.WaitForOutputAsync(output => output.Contains("Outbox relay polling", StringComparison.Ordinal), TimeSpan.FromSeconds(60));

        // As contas em paralelo, liberadas juntas; dentro de cada conta, um crédito depois do outro.
        await StartGate.RunTogetherAsync(Accounts, async index =>
        {
            for (var credit = 1; credit <= CreditsPerAccount; credit++)
            {
                await environment.Client.CreditOkAsync(accounts[index].Id, $"{credit}.00");
            }

            return true;
        });

        foreach (var account in accounts)
        {
            await environment.WaitProjectedAsync(first, account.Id, CreditsPerAccount);
        }

        var total = 0;
        foreach (var account in accounts)
        {
            var row = (await environment.Rows.AccountAsync(account.Id)).ShouldNotBeNull();
            var position = (await environment.Mongo.PositionAsync(account.Id)).ShouldNotBeNull();
            var sequences = (await environment.Mongo.EntriesAsync(account.Id)).Select(document => document["sequence"].AsInt64).ToList();

            position["version"].AsInt64.ShouldBe(row.Version);
            position["balance"].AsDecimal.ShouldBe(row.Balance);
            sequences.ShouldBe(Enumerable.Range(1, (int)row.Version).Select(sequence => (long)sequence));
            total += sequences.Count;
            await Eventually.WaitUntilAsync(
                async () => (await environment.Database.OutboxAsync(account.Id)).TrueForAll(@event => @event.ProcessedAt != null),
                TimeSpan.FromSeconds(30),
                () => $"events of {account.Id} still pending",
                TimeSpan.FromMilliseconds(100));
            (await environment.Database.OutboxAsync(account.Id)).Count.ShouldBe(CreditsPerAccount);
        }

        total.ShouldBe(Accounts * CreditsPerAccount);
        first.HasExited.ShouldBeFalse();
        second.HasExited.ShouldBeFalse();
        // As duas réplicas trabalharam.
        first.Output.ShouldContain("Outbox relay claimed");
        second.Output.ShouldContain("Outbox relay claimed");
    }
}
