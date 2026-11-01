using System.Net;
using System.Text;
using IntegrationTests.Infrastructure;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class VaultRestartTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task RestartedVaultUnsealsAndKeepsKeys()
    {
        var id = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        using var before = await environment.Client.PersonalDataAsync(id, CustomersTokens.Compliance);
        var expected = (await before.ShouldBeCustomerAsync()).GetRawText();
        var dek = await environment.Vault.DekAsync(id);

        await environment.Vault.RestartAsync();
        var status = await environment.Vault.StatusAsync();
        // Uma Api nova, com o cache vazio: a leitura precisa da DEK guardada no cofre reiniciado.
        using var fresh = environment.CreateClient(new LogSink());
        using var after = await fresh.PersonalDataAsync(id, CustomersTokens.Compliance);

        status.ExitCode.ShouldBe(0, status.Stdout + status.Stderr);
        status.Stdout.ShouldMatch(@"Sealed\s+false");
        status.Stdout.ShouldMatch(@"Seal Type\s+static");
        status.Stdout.ShouldMatch(@"Storage Type\s+raft");
        (await environment.Vault.DekAsync(id)).ShouldBe(dek);
        (await after.ShouldBeCustomerAsync()).GetRawText().ShouldBe(expected);
    }
}

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class VaultBootstrapTests(CustomersEnvironment environment)
{
    private static readonly byte[] Cpf = Encoding.UTF8.GetBytes("52998224725");

    [Fact]
    public async Task SecondRunKeepsTheKeys()
    {
        var (indexBefore, _) = await environment.Vault.HmacAsync("customers-blind-index", Cpf);
        var (hashBefore, _) = await environment.Vault.HmacAsync("customers-request-hash", Cpf);

        var rerun = await environment.Vault.BootstrapAsync();

        rerun.ExitCode.ShouldBe(0, rerun.Stdout + rerun.Stderr);
        (await environment.Vault.LatestVersionAsync("customers-blind-index")).ShouldBe(1);
        (await environment.Vault.LatestVersionAsync("customers-request-hash")).ShouldBe(1);
        (await environment.Vault.HmacAsync("customers-blind-index", Cpf)).Value.ShouldBe(indexBefore);
        (await environment.Vault.HmacAsync("customers-request-hash", Cpf)).Value.ShouldBe(hashBefore);
        // O AppRole segue funcionando com as mesmas credenciais.
        (await environment.Vault.AppRoleTokenAsync()).ShouldNotBeNullOrWhiteSpace();
    }
}

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class VaultPolicyTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task AppRoleHasOnlyItsCapabilities()
    {
        var token = await environment.Vault.AppRoleTokenAsync();
        var customer = Guid.NewGuid();
        var dek = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var input = new { input = Convert.ToBase64String(Encoding.UTF8.GetBytes("52998224725")) };

        var allowed = new List<(string Name, HttpStatusCode Status)>();
        allowed.Add(("create DEK", await StatusAsync(HttpMethod.Post, $"customers/data/dek/{customer:D}", token, new { options = new { cas = 0 }, data = new { key = dek } })));
        allowed.Add(("read DEK", await StatusAsync(HttpMethod.Get, $"customers/data/dek/{customer:D}", token)));
        allowed.Add(("blind index", await StatusAsync(HttpMethod.Post, "transit/hmac/customers-blind-index", token, input)));
        allowed.Add(("request hash", await StatusAsync(HttpMethod.Post, "transit/hmac/customers-request-hash", token, input)));

        var refused = new List<(string Name, HttpStatusCode Status)>
        {
            ("export", await StatusAsync(HttpMethod.Get, "transit/export/hmac-key/customers-blind-index", token)),
            ("delete DEK", await StatusAsync(HttpMethod.Delete, $"customers/data/dek/{customer:D}", token)),
            ("delete metadata", await StatusAsync(HttpMethod.Delete, $"customers/metadata/dek/{customer:D}", token)),
            ("overwrite DEK", await StatusAsync(
                HttpMethod.Post, $"customers/data/dek/{customer:D}", token, new { options = new { cas = 0 }, data = new { key = "AAAA" } })),
            ("overwrite without cas", await StatusAsync(HttpMethod.Post, $"customers/data/dek/{customer:D}", token, new { data = new { key = "AAAA" } })),
            ("other path", await StatusAsync(HttpMethod.Get, "customers/data/outro/x", token)),
            ("rotate", await StatusAsync(HttpMethod.Post, "transit/keys/customers-blind-index/rotate", token)),
            ("policies", await StatusAsync(HttpMethod.Get, "sys/policies/acl", token)),
        };

        allowed.ShouldAllBe(result => result.Status == HttpStatusCode.OK || result.Status == HttpStatusCode.NoContent);
        refused.ShouldAllBe(result => result.Status == HttpStatusCode.Forbidden || (result.Name.StartsWith("overwrite") && result.Status == HttpStatusCode.BadRequest));
        (await environment.Vault.DekAsync(customer)).ShouldBe(Convert.FromBase64String(dek));
        (await environment.Vault.LatestVersionAsync("customers-blind-index")).ShouldBe(1);
    }

    private async Task<HttpStatusCode> StatusAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var response = await environment.Vault.SendAsync(method, path, token, body);
        return response.StatusCode;
    }
}
