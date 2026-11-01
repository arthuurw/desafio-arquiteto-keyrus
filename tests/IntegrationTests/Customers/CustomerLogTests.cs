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

        var id = await client.RegisteredAsync(customer);
        using var duplicate = await client.RegisterAsync(customer with { Name = "Outro Nome", Email = "outro@ledger.local" });
        using var invalid = await client.RegisterAsync(customer with { BirthDate = "2020-01-01" });
        using var masked = await client.MaskedAsync(id, CustomersTokens.Backoffice);
        using var complete = await client.PersonalDataAsync(id, CustomersTokens.Compliance);
        var tampered = await client.RegisteredAsync(tamperedCustomer);
        var row = (await environment.Database.CustomerAsync(tampered)).ShouldNotBeNull();
        await environment.Database.OverwriteFieldAsync(tampered, "cpf", row.Email);
        using var broken = await client.PersonalDataAsync(tampered, CustomersTokens.Compliance);

        await duplicate.ShouldBeProblemAsync(409, "cpf-already-registered");
        await invalid.ShouldBeValidationErrorAsync("birthDate");
        await masked.ShouldBeCustomerAsync();
        await complete.ShouldBeCustomerAsync();
        await broken.ShouldBeProblemAsync(500, "internal-error");
        var lines = log.Since(0);
        lines.Count.ShouldBeGreaterThan(0);
        string[] personal =
        [
            customer.Name, customer.Cpf, Formatted(customer.Cpf), customer.BirthDate, customer.Email,
            tamperedCustomer.Name, tamperedCustomer.Cpf, Formatted(tamperedCustomer.Cpf), tamperedCustomer.BirthDate, tamperedCustomer.Email,
        ];
        foreach (var line in lines)
        {
            foreach (var value in personal)
            {
                line.Message.ShouldNotContain(value, Case.Insensitive, $"{line.Category}: {line.Message}");
                (line.Exception ?? string.Empty).ShouldNotContain(value, Case.Insensitive, $"{line.Category}: {line.Exception}");
            }
        }
    }

    private static string Formatted(string digits) => $"{digits[..3]}.{digits[3..6]}.{digits[6..9]}-{digits[9..]}";
}
