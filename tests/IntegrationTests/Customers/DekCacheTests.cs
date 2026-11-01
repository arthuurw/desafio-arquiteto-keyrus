using System.Diagnostics;
using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class DekCacheTests(CustomersEnvironment environment)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task CachedKeyServesReadsWhileVaultIsDown()
    {
        var a = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var b = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        using var client = environment.CreateClient(new LogSink(), dekCacheTtl: "00:00:03");

        var sinceVaultRead = Stopwatch.StartNew();
        using (var warm = await client.MaskedAsync(a, CustomersTokens.Backoffice))
        {
            await warm.ShouldBeCustomerAsync();
        }

        await using (await environment.Vault.PauseAsync())
        {
            using var masked = await client.MaskedAsync(a, CustomersTokens.Backoffice);
            using var complete = await client.PersonalDataAsync(a, CustomersTokens.Compliance);
            var cachedReadsAt = sinceVaultRead.Elapsed;
            using var neverRead = await client.MaskedAsync(b, CustomersTokens.Backoffice);

            await masked.ShouldBeCustomerAsync();
            await complete.ShouldBeCustomerAsync();
            cachedReadsAt.ShouldBeLessThan(Ttl);
            await neverRead.ShouldBeProblemAsync(503, "personal-data-unavailable");
            neverRead.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(5));

            // A espera aqui é o próprio prazo do cache: a leitura de A passa a precisar do cofre.
            HttpResponseMessage expired = null!;
            await Eventually.WaitUntilAsync(
                async () =>
                {
                    expired?.Dispose();
                    expired = await client.PersonalDataAsync(a, CustomersTokens.Compliance);
                    return (int)expired.StatusCode == 503;
                },
                TimeSpan.FromSeconds(15),
                () => "the cached key never expired",
                TimeSpan.FromMilliseconds(200));
            using (expired)
            {
                sinceVaultRead.Elapsed.ShouldBeGreaterThanOrEqualTo(Ttl);
                await expired.ShouldBeProblemAsync(503, "personal-data-unavailable");
            }
        }
    }
}
