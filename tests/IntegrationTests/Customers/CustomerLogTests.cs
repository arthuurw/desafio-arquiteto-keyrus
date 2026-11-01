using System.Text.Json;
using IntegrationTests.Identity;
using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class CustomerLogTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task NoPersonalDataInLogs()
    {
        // Uma Api só deste teste, com todo log em todo nível: nada do que ela escreve fica de fora.
        var log = new LogSink();
        using var client = environment.CreateClient(log);
        var customer = new CustomerInput("Elisa Fontes Barreto", CustomersClient.NewCpf(), "1983-05-27", $"elisa.{Guid.NewGuid():N}@ledger.local");
        var tamperedCustomer = CustomersClient.NewCustomer() with { Name = "Fabio Rangel Torres" };
        var refusedCustomer = new CustomerInput("Gilda Souto Paiva", CustomersClient.NewCpf(), "1991-08-14", $"gilda.{Guid.NewGuid():N}@ledger.local");
        var outageCustomer = new CustomerInput("Hugo Lessa Brandao", CustomersClient.NewCpf(), "1979-10-02", $"hugo.{Guid.NewGuid():N}@ledger.local");

        var id = await client.RegisteredAsync(customer);
        using var duplicate = await client.RegisterAsync(customer with { Name = "Outro Nome", Email = "outro@ledger.local" });
        using var invalid = await client.RegisterAsync(customer with { BirthDate = "2020-01-01" });
        using var malformed = await client.SendRawAsync(
            HttpMethod.Post, "/v1/customers", $"{{\"name\": \"{refusedCustomer.Name}\", \"cpf\": \"{refusedCustomer.Cpf}\", ", CustomersTokens.Backoffice);
        using var anonymousRegistration = await client.SendJsonAsync(HttpMethod.Post, "/v1/customers", Body(refusedCustomer), token: null);
        using var delegatedRegistration = await client.SendJsonAsync(HttpMethod.Post, "/v1/customers", Body(refusedCustomer), CustomersTokens.Delegated(id));
        using var masked = await client.MaskedAsync(id, CustomersTokens.Backoffice);
        using var complete = await client.PersonalDataAsync(id, CustomersTokens.Compliance);
        using var anonymousRead = await client.PersonalDataAsync(id, token: null);
        using var scopelessRead = await client.PersonalDataAsync(id, CustomersTokens.Backoffice);
        var tampered = await client.RegisteredAsync(tamperedCustomer);
        var row = (await environment.Database.CustomerAsync(tampered)).ShouldNotBeNull();
        await environment.Database.OverwriteFieldAsync(tampered, "cpf", row.Email);
        using var broken = await client.PersonalDataAsync(tampered, CustomersTokens.Compliance);
        HttpResponseMessage unaudited;
        await using (await environment.Database.RefuseAuditInsertAsync("customers", id))
        {
            unaudited = await client.PersonalDataAsync(id, CustomersTokens.Compliance);
        }

        var account = await client.OpenAccountAsync(id);
        var credit = await client.CreditOkAsync(account.Id, "100.00");
        var debit = await client.DebitOkAsync(account, "40.00");
        HttpResponseMessage outage;
        await using (await environment.Vault.StopAsync())
        {
            outage = await client.RegisterAsync(outageCustomer);
        }

        await duplicate.ShouldBeProblemAsync(409, "cpf-already-registered");
        await invalid.ShouldBeValidationErrorAsync("birthDate");
        ((int)malformed.StatusCode).ShouldBe(400);
        await anonymousRegistration.ShouldBeProblemAsync(401, "authentication-required");
        await delegatedRegistration.ShouldBeProblemAsync(403, "insufficient-scope");
        await masked.ShouldBeCustomerAsync();
        await complete.ShouldBeCustomerAsync();
        await anonymousRead.ShouldBeProblemAsync(401, "authentication-required");
        await scopelessRead.ShouldBeProblemAsync(403, "insufficient-scope");
        await broken.ShouldBeProblemAsync(500, "internal-error");
        await unaudited.ShouldBeProblemAsync(500, "internal-error");
        account.CustomerId.ShouldBe(id);
        credit.GetProperty("id").ValueKind.ShouldBe(JsonValueKind.String);
        debit.GetProperty("id").ValueKind.ShouldBe(JsonValueKind.String);
        await outage.ShouldBeProblemAsync(503, "personal-data-unavailable");
        unaudited.Dispose();
        outage.Dispose();

        var lines = log.Since(0);
        lines.Count.ShouldBeGreaterThan(0);
        string[] personal = [.. new[] { customer, tamperedCustomer, refusedCustomer, outageCustomer }.SelectMany(Values)];
        foreach (var line in lines)
        {
            foreach (var value in personal)
            {
                line.Message.ShouldNotContain(value, Case.Insensitive, $"{line.Category}: {line.Message}");
                (line.Exception ?? string.Empty).ShouldNotContain(value, Case.Insensitive, $"{line.Category}: {line.Exception}");
            }
        }
    }

    private static object Body(CustomerInput customer) =>
        new { name = customer.Name, cpf = customer.Cpf, birthDate = customer.BirthDate, email = customer.Email };

    private static string[] Values(CustomerInput customer) =>
        [customer.Name, customer.Cpf, Formatted(customer.Cpf), customer.BirthDate, customer.Email];

    private static string Formatted(string digits) => $"{digits[..3]}.{digits[3..6]}.{digits[6..9]}-{digits[9..]}";
}
