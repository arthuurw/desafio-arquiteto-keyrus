using System.Text;
using System.Text.Json;
using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;

namespace IntegrationTests.Customers;

// O HMAC do Transit usa a versão configurada da chave, não a mais nova: depois de um `rotate`, o mesmo CPF e a mesma
// requisição seguem com o mesmo valor. Cada teste rotaciona num cofre próprio: no da coleção, a rotação mudaria os
// valores dos outros testes.
[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class KeyVersionTests(CustomersEnvironment environment)
{
    private const string BlindIndexKey = "customers-blind-index";
    private const string RequestHashKey = "customers-request-hash";

    [Fact]
    public async Task ConfiguredVersionIsTheOneStored()
    {
        await using var vault = await OpenBaoVault.StartAsync();
        await vault.RotateAsync(BlindIndexKey);
        await vault.RotateAsync(RequestHashKey);
        (await vault.LatestVersionAsync(BlindIndexKey)).ShouldBe(2);
        (await vault.LatestVersionAsync(RequestHashKey)).ShouldBe(2);
        using var client = environment.CreateClient(new LogSink(), vault.Address);
        var customer = CustomersClient.NewCustomer();
        var key = Guid.NewGuid();

        using var response = await client.RegisterAsync(customer, key.ToString());
        var id = (await response.ShouldBeCreatedAsync())["id"]!.GetValue<Guid>();

        var row = (await environment.Database.CustomerAsync(id)).ShouldNotBeNull();
        var digits = Encoding.UTF8.GetBytes(customer.Cpf);
        row.CpfBlindIndexVersion.ShouldBe(1);
        row.CpfBlindIndex.ShouldBe((await vault.HmacAsync(BlindIndexKey, digits, version: 1)).Value);
        row.CpfBlindIndex.ShouldNotBe((await vault.HmacAsync(BlindIndexKey, digits, version: 2)).Value);
        var storedHash = (await environment.Database.ScalarAsync<byte[]>(
            "postgres", $"SELECT request_hash FROM customers.idempotency_keys WHERE idempotency_key = '{key}'")).ShouldNotBeNull();
        var payload = RequestPayload(customer);
        storedHash.ShouldBe((await vault.HmacAsync(RequestHashKey, payload, version: 1)).Value);
        storedHash.ShouldNotBe((await vault.HmacAsync(RequestHashKey, payload, version: 2)).Value);
    }

    [Fact]
    public async Task RotatedBlindIndexKeyStillFindsTheCpf()
    {
        await using var vault = await OpenBaoVault.StartAsync();
        using var client = environment.CreateClient(new LogSink(), vault.Address);
        var customer = CustomersClient.NewCustomer();
        await client.RegisteredAsync(customer);
        await vault.RotateAsync(BlindIndexKey);
        var customersBefore = await environment.Database.CustomerCountAsync();

        using var again = await client.RegisterAsync(customer, Guid.NewGuid().ToString());

        await again.ShouldBeProblemAsync(409, "cpf-already-registered");
        (await environment.Database.CustomerCountAsync()).ShouldBe(customersBefore);
    }

    [Fact]
    public async Task RotatedRequestHashKeyStillReplays()
    {
        await using var vault = await OpenBaoVault.StartAsync();
        using var client = environment.CreateClient(new LogSink(), vault.Address);
        var customer = CustomersClient.NewCustomer();
        var key = Guid.NewGuid().ToString();
        using var first = await client.RegisterAsync(customer, key);
        var id = (await first.ShouldBeCreatedAsync())["id"]!.GetValue<Guid>();
        await vault.RotateAsync(RequestHashKey);

        using var replay = await client.RegisterAsync(customer, key);

        ((int)replay.StatusCode).ShouldBe(201);
        replay.IsReplay().ShouldBeTrue();
        (await replay.ShouldBeCreatedAsync())["id"]!.GetValue<Guid>().ShouldBe(id);
    }

    // A entrada do hash de um cadastro pelo backoffice: operação, nome, CPF, nascimento, e-mail e o modo do chamador.
    private static byte[] RequestPayload(CustomerInput customer) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new[] { "register-customer", customer.Name, customer.Cpf, customer.BirthDate, customer.Email, "service", string.Empty });
}
