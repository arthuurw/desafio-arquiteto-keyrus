using System.Text.Json;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class CustomerRegistrationTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task RegistrationReturnsOnlyTheId()
    {
        var ana = await environment.AnaAsync();

        ana.Status.ShouldBe(201);
        ana.Replayed.ShouldBeFalse();
        var body = JsonDocument.Parse(ana.Body).RootElement;
        body.EnumerateObject().Select(property => property.Name).ShouldBe(["id"]);
        var id = body.GetProperty("id").GetGuid();
        id.ShouldNotBe(Guid.Empty);
        (await environment.Database.CustomerAsync(id)).ShouldNotBeNull().Id.ShouldBe(id);
    }
}
