using System.Diagnostics;
using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class DekCacheTests(CustomersEnvironment environment)
{
    // Folga para pausar o cofre e ler duas vezes antes de o cache expirar, também com a máquina carregada.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CachedKeyServesReadsWhileVaultIsDown()
    {
        var a = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var b = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        // O cofre pausado não responde: a leitura que precisa dele estoura o prazo de produção.
        using var client = environment.CreateClient(
            new LogSink(), dekCacheTtl: Ttl.ToString("c"), vaultTimeout: TestTimeouts.ProductionVault);

        // A primeira leitura faz o login no cofre e pode passar de 1 s com a máquina carregada: repete até guardar a chave.
        var sinceVaultRead = Stopwatch.StartNew();
        await Eventually.WaitUntilAsync(
            async () =>
            {
                using var warm = await client.MaskedAsync(a, CustomersTokens.Backoffice);
                return (int)warm.StatusCode == 200;
            },
            TimeSpan.FromSeconds(30),
            () => "the Api never read the customer through the vault",
            TimeSpan.FromMilliseconds(200));

        await using (await environment.Vault.PauseAsync())
        {
            using var masked = await client.MaskedAsync(a, CustomersTokens.Backoffice);
            using var complete = await client.PersonalDataAsync(a, CustomersTokens.Compliance);
            using var neverRead = await client.MaskedAsync(b, CustomersTokens.Backoffice);

            await masked.ShouldBeCustomerAsync();
            await complete.ShouldBeCustomerAsync();
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
                TimeSpan.FromSeconds(30),
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
