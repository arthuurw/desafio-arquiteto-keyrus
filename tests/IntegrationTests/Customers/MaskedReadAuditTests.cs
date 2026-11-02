using IntegrationTests.Infrastructure;

namespace IntegrationTests.Customers;

// A leitura mascarada também decifra os dados pessoais: toda resposta deixa uma linha `read-customer` em
// `customers.audit_events` (ADR-0013, ADR-0041).
[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class MaskedReadAuditTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task EveryMaskedReadIsRecorded()
    {
        var id = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var missing = Guid.NewGuid();
        using var vaultDown = environment.CreateClient(new LogSink(), UnreachableVault.Address);

        var found = await ReadAsync(environment.Client, id.ToString(), CustomersTokens.Backoffice);
        var notFound = await ReadAsync(environment.Client, missing.ToString(), CustomersTokens.Backoffice);
        var invalidId = await ReadAsync(environment.Client, "abc", CustomersTokens.Backoffice);
        var withoutScope = await ReadAsync(environment.Client, id.ToString(), CustomersTokens.Only("customers.write"));
        var anonymous = await ReadAsync(environment.Client, id.ToString(), token: null);
        var unavailable = await ReadAsync(vaultDown, id.ToString(), CustomersTokens.Backoffice);

        found.Status.ShouldBe(200, found.Body);
        (await RowAsync(found)).ShouldBe(Row("backoffice", id, 200));
        (notFound.Status, notFound.ProblemType).ShouldBe((404, "customer-not-found"));
        (await RowAsync(notFound)).ShouldBe(Row("backoffice", missing, 404));
        (invalidId.Status, invalidId.ProblemType).ShouldBe((404, "customer-not-found"));
        (await RowAsync(invalidId)).ShouldBe(Row("backoffice", null, 404));
        (withoutScope.Status, withoutScope.ProblemType).ShouldBe((403, "insufficient-scope"));
        (await RowAsync(withoutScope)).ShouldBe(Row("backoffice", id, 403));
        (anonymous.Status, anonymous.ProblemType).ShouldBe((401, "authentication-required"));
        (await RowAsync(anonymous)).ShouldBe(Row(null, id, 401));
        (unavailable.Status, unavailable.ProblemType).ShouldBe((503, "personal-data-unavailable"));
        (await RowAsync(unavailable)).ShouldBe(Row("backoffice", id, 503));
    }

    private Task<TracedResponse> ReadAsync(HttpClient client, string id, string? token) =>
        client.SendTracedAsync(environment.Completions, HttpMethod.Get, $"/v1/customers/{id}", body: null, token);

    // Exatamente uma linha por resposta, com o `trace_id` da requisição.
    private Task<AuditRow> RowAsync(TracedResponse response) => environment.Database.CheckedAuditRowAsync("customers", response);

    private static AuditRow Row(string? client, Guid? resource, int result) =>
        AuditRow.Expected(client, null, "read-customer", "customer", resource, result);
}
