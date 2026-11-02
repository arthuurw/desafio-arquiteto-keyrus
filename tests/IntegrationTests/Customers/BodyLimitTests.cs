using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;
using static IntegrationTests.Infrastructure.AuditRows;
using static IntegrationTests.Infrastructure.LargeBodies;

namespace IntegrationTests.Customers;

// Corpo acima de 16 384 bytes no cadastro: `413` antes da leitura do JSON, sem consumir a chave, com linha na trilha.
[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class BodyLimitTests(CustomersEnvironment environment)
{
    private const string Route = "/v1/customers";

    [Fact]
    public async Task BodyAboveTheLimitIsRefused()
    {
        var customersBefore = await environment.Database.CustomerCountAsync();

        using var atLimit = await environment.Client.PostAsync(
            Route, Padded(CustomersClient.NewCustomer().RegistrationBody, Limit), CustomersTokens.Backoffice, NewKey());
        using var overLimit = await environment.Client.PostAsync(Route, NotJson(Limit + 1), CustomersTokens.Backoffice, NewKey());
        using var huge = await environment.Client.PostAsync(Route, NotJson(30 * 1024 * 1024), CustomersTokens.Backoffice, NewKey());
        using var chunked = await environment.Client.PostAsync(
            Route, Padded(CustomersClient.NewCustomer().RegistrationBody, Limit + 1), CustomersTokens.Backoffice, NewKey(), chunked: true);

        await atLimit.ShouldBeCreatedAsync();
        foreach (var refused in new[] { overLimit, huge, chunked })
        {
            var problem = await refused.ShouldBeProblemAsync(413, "payload-too-large");
            problem.GetProperty("title").GetString().ShouldBe("Corpo da requisição grande demais");
        }

        (await environment.Database.CustomerCountAsync()).ShouldBe(customersBefore + 1);
    }

    [Fact]
    public async Task RefusedBodyDoesNotConsumeTheKey()
    {
        var key = NewKey();

        using var refused = await environment.Client.PostAsync(Route, NotJson(Limit + 1), CustomersTokens.Backoffice, key);
        using var accepted = await environment.Client.RegisterAsync(CustomersClient.NewCustomer(), key);

        await refused.ShouldBeProblemAsync(413, "payload-too-large");
        await accepted.ShouldBeCreatedAsync();
        accepted.IsReplay().ShouldBeFalse();
    }

    [Fact]
    public async Task RefusedBodyIsAudited()
    {
        var refused = await environment.Client.SendTracedAsync(
            environment.Completions, HttpMethod.Post, Route, body: null, CustomersTokens.Backoffice, NewKey(), rawJson: NotJson(Limit + 1));

        (refused.Status, refused.ProblemType).ShouldBe((413, "payload-too-large"));
        (await environment.Database.CheckedAuditRowAsync("customers", refused))
            .ShouldBe(AuditRow.Expected("backoffice", null, "register-customer", "customer", null, 413));
    }
}
