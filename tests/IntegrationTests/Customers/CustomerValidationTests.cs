using IntegrationTests.Ledger;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class CustomerValidationTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task InvalidFieldsAreReportedTogetherWithoutConsumingTheKey()
    {
        var key = Guid.NewGuid().ToString();
        var valid = CustomersClient.NewCustomer();
        var customersBefore = await environment.Database.CustomerCountAsync();
        var keysBefore = await environment.Database.KeyCountAsync();

        using var allInvalid = await environment.Client.RegisterAsync(new CustomerInput("", "111.111.111-11", "ontem", "a@b"), key);
        using var missingEmail = await environment.Client.SendRawAsync(
            HttpMethod.Post, "/v1/customers", $$"""{ "name": "Ana", "cpf": "{{valid.Cpf}}", "birthDate": "1990-04-15" }""", CustomersTokens.Backoffice, key);
        using var numericCpf = await environment.Client.SendRawAsync(
            HttpMethod.Post,
            "/v1/customers",
            """{ "name": "Ana", "cpf": 52998224725, "birthDate": "1990-04-15", "email": "a@b.co" }""",
            CustomersTokens.Backoffice,
            key);
        var keysAfterRefusals = await environment.Database.KeyCountAsync();
        var customersAfterRefusals = await environment.Database.CustomerCountAsync();
        using var accepted = await environment.Client.RegisterAsync(valid, key);

        (await ErrorFieldsAsync(allInvalid)).ShouldBe(["birthDate", "cpf", "email", "name"]);
        (await ErrorFieldsAsync(missingEmail)).ShouldBe(["email"]);
        (await ErrorFieldsAsync(numericCpf)).ShouldBe(["cpf"]);
        keysAfterRefusals.ShouldBe(keysBefore);
        customersAfterRefusals.ShouldBe(customersBefore);
        await accepted.ShouldBeCreatedAsync();
        accepted.IsReplay().ShouldBeFalse();
    }

    private static async Task<List<string>> ErrorFieldsAsync(HttpResponseMessage response)
    {
        var body = await response.ShouldBeProblemAsync(400, "validation-error");
        return body.GetProperty("errors").EnumerateObject().Select(error => error.Name).Order(StringComparer.Ordinal).ToList();
    }
}
