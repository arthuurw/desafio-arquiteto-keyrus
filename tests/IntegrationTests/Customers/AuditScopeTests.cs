using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;

namespace IntegrationTests.Customers;

// Só escritas e a leitura completa são auditadas: as leituras de Positions, a mascarada e a liveness não (ADR-0041).
[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class AuditScopeTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task ReadRoutesRecordNothing()
    {
        var customer = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var account = await environment.OpenAccountAsync(customer);
        var channel = LedgerTokens.Channel(customer);

        var reads = new[]
        {
            await GetAsync($"/v1/accounts/{account.Id}/balance", channel),
            await GetAsync($"/v1/accounts/{account.Id}/statement", channel),
            await GetAsync($"/v1/customers/{customer}", CustomersTokens.Backoffice),
            await GetAsync($"/v1/customers/{customer}", token: null),
            await GetAsync("/health/live", token: null),
        };

        reads.Select(read => read.Status).ShouldBe([200, 200, 200, 401, 200]);
        (await environment.Database.AuditRowCountAsync(reads.Select(read => read.Trace))).ShouldBe(0);
    }

    // O contrato navegável só existe em Development: uma Api nesse ambiente, sobre o mesmo banco.
    [Fact]
    public async Task OpenApiRecordsNothing()
    {
        using var development = environment.CreateClient(new LogSink(), hostEnvironment: "Development");

        var reads = new[]
        {
            await development.SendTracedAsync(environment.Completions, HttpMethod.Get, "/openapi/v1.json", body: null, token: null),
            await development.SendTracedAsync(environment.Completions, HttpMethod.Get, "/scalar", body: null, token: null),
        };

        reads.Select(read => read.Status).ShouldBe([200, 200]);
        (await environment.Database.AuditRowCountAsync(reads.Select(read => read.Trace))).ShouldBe(0);
    }

    private Task<TracedResponse> GetAsync(string route, string? token) =>
        environment.Client.SendTracedAsync(environment.Completions, HttpMethod.Get, route, body: null, token);
}
