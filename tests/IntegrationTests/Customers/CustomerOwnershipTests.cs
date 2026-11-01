using IntegrationTests.Ledger;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class CustomerOwnershipTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task OtherCustomerLooksMissing()
    {
        var a = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var b = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var token = CustomersTokens.Delegated(a);

        using var missing = await environment.Client.MaskedAsync(Guid.NewGuid(), token);
        var reference = await missing.ShouldBeProblemAsync(404, "customer-not-found");
        foreach (var route in new Func<object, string, Task<HttpResponseMessage>>[] { environment.Client.MaskedAsync, environment.Client.PersonalDataAsync })
        {
            using var other = await route(b, token);
            using var own = await route(a, token);
            using var malformed = await route("not-a-uuid", token);

            var otherBody = await other.ShouldBeProblemAsync(404, "customer-not-found");
            otherBody.GetProperty("title").GetString().ShouldBe(reference.GetProperty("title").GetString());
            otherBody.GetProperty("detail").GetString().ShouldBe(reference.GetProperty("detail").GetString());
            (await own.ShouldBeCustomerAsync()).GetProperty("id").GetGuid().ShouldBe(a);
            var malformedBody = await malformed.ShouldBeProblemAsync(404, "customer-not-found");
            malformedBody.GetProperty("title").GetString().ShouldBe(reference.GetProperty("title").GetString());
            malformedBody.GetProperty("detail").GetString().ShouldBe(reference.GetProperty("detail").GetString());
        }
    }
}
