using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntegrationTests.Ledger;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class CustomerIdempotencyTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task KeyIsRequired()
    {
        var customersBefore = await environment.Database.CustomerCountAsync();
        var keysBefore = await environment.Database.KeyCountAsync();

        using var missing = await environment.Client.RegisterAsync(CustomersClient.NewCustomer(), KeyHeader.None);
        await missing.ShouldBeProblemAsync(400, "idempotency-key-missing");
        foreach (var invalid in new[] { "abc", $"{{{Guid.NewGuid()}}}" })
        {
            using var response = await environment.Client.RegisterAsync(CustomersClient.NewCustomer(), invalid);
            await response.ShouldBeProblemAsync(400, "idempotency-key-invalid");
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/customers", UriKind.Relative)))
        {
            var customer = CustomersClient.NewCustomer();
            request.Content = JsonContent(customer);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CustomersTokens.Backoffice);
            request.Headers.TryAddWithoutValidation("Idempotency-Key", [Guid.NewGuid().ToString(), Guid.NewGuid().ToString()]);
            using var twoValues = await environment.Client.SendAsync(request, TestContext.Current.CancellationToken);
            await twoValues.ShouldBeProblemAsync(400, "idempotency-key-invalid");
        }

        (await environment.Database.CustomerCountAsync()).ShouldBe(customersBefore);
        (await environment.Database.KeyCountAsync()).ShouldBe(keysBefore);
    }

    [Fact]
    public async Task ReplayReturnsSameId()
    {
        var ana = await environment.AnaAsync();
        var deksBefore = await environment.Vault.DekIdsAsync();

        using var replay = await environment.Client.RegisterAsync(CustomersClient.Ana with { Cpf = "52998224725" }, ana.Key.ToString());

        var body = await replay.ShouldBeCreatedAsync();
        body["id"]!.GetValue<Guid>().ShouldBe(ana.Id);
        replay.IsReplay().ShouldBeTrue();
        var index = (await environment.Database.CustomerAsync(ana.Id)).ShouldNotBeNull().CpfBlindIndex;
        (await environment.Database.CustomersWithIndexAsync(index)).ShouldBe(1);
        var deks = await environment.Vault.DekIdsAsync();
        deks.Count(dek => dek == ana.Id.ToString("D")).ShouldBe(1);
        deks.Order().ShouldBe(deksBefore.Order());
    }

    [Fact]
    public async Task ChangedFieldWithSameKeyIsRefused()
    {
        var ana = await environment.AnaAsync();
        var before = await environment.Database.CustomerCountAsync();

        foreach (var changed in new[]
        {
            CustomersClient.Ana with { Name = "Ana Maria Souza Lima" },
            CustomersClient.Ana with { Cpf = "111.444.777-35" },
            CustomersClient.Ana with { BirthDate = "1990-04-16" },
            CustomersClient.Ana with { Email = "ana@ledger.local" },
        })
        {
            using var response = await environment.Client.RegisterAsync(changed, ana.Key.ToString());
            await response.ShouldBeProblemAsync(422, "idempotency-key-reused");
        }

        (await environment.Database.CustomerCountAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task RequestHashIsKeyedHmac()
    {
        var ana = await environment.AnaAsync();
        var fields = Encoding.UTF8.GetBytes(
            """["register-customer","Ana Maria Souza","52998224725","1990-04-15","ana.souza@ledger.local","service",""]""");

        var stored = (await environment.Database.RequestHashAsync("backoffice", ana.Key)).ShouldNotBeNull();
        var (hmac, _) = await environment.Vault.HmacAsync("customers-request-hash", fields);

        stored.ShouldBe(hmac);
        stored.ShouldNotBe(SHA256.HashData(fields));
    }

    private static StringContent JsonContent(CustomerInput customer) =>
        new(
            JsonSerializer.Serialize(new { name = customer.Name, cpf = customer.Cpf, birthDate = customer.BirthDate, email = customer.Email }),
            Encoding.UTF8,
            "application/json");
}
