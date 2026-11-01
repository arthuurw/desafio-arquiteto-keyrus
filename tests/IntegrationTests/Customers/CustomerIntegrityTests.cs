using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;
using Microsoft.Extensions.Logging;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class CustomerIntegrityTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task TamperedFieldFailsTheRead()
    {
        var a = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var b = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var c = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var rowA = (await environment.Database.CustomerAsync(a)).ShouldNotBeNull();
        var rowC = (await environment.Database.CustomerAsync(c)).ShouldNotBeNull();

        // CPF de A no CPF de B: mesmo campo, outro cliente.
        await environment.Database.OverwriteFieldAsync(b, "cpf", rowA.Cpf);
        await ShouldFailIntegrityAsync(b, "cpf", rowA.Cpf);

        // E-mail de A no nome de A: mesmo cliente, outro campo.
        await environment.Database.OverwriteFieldAsync(a, "name", rowA.Email);
        await ShouldFailIntegrityAsync(a, "name", rowA.Email);

        // Último byte do CPF de C trocado: a tag não confere.
        var tampered = (byte[])rowC.Cpf.Clone();
        tampered[^1] ^= 0xFF;
        await environment.Database.OverwriteFieldAsync(c, "cpf", tampered);
        await ShouldFailIntegrityAsync(c, "cpf", tampered);
    }

    private async Task ShouldFailIntegrityAsync(Guid customerId, string field, byte[] stored)
    {
        var mark = environment.ApiLog.Mark();

        using var response = await environment.Client.PersonalDataAsync(customerId, CustomersTokens.Compliance);

        var body = await response.ShouldBeProblemAsync(500, "internal-error");
        body.EnumerateObject().Select(property => property.Name).Order()
            .ShouldBe(["detail", "status", "title", "traceId", "type"]);
        var errors = environment.ApiLog.Since(mark).Where(line => line.Level == LogLevel.Error).ToList();
        var integrity = errors.ShouldHaveSingleItem($"{customerId} {field}");
        integrity.Message.ShouldContain(customerId.ToString("D"));
        integrity.Message.ShouldContain(field);
        integrity.Message.ShouldNotContain(Convert.ToBase64String(stored));
        integrity.Message.ShouldNotContain(Convert.ToHexString(stored), Case.Insensitive, $"{customerId} {field}");
        integrity.Message.ShouldNotContain(Convert.ToBase64String(stored[1..13]));
    }
}
