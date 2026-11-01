using System.Text;
using IntegrationTests.Identity;
using IntegrationTests.Infrastructure;

namespace IntegrationTests.Customers;

// Todo cadastro de cliente deixa uma linha em `customers.audit_events`, com o desfecho devolvido.
[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class RegistrationAuditTests(CustomersEnvironment environment)
{
    private const string TraceChecked = "checked";

    [Fact]
    public async Task RegistrationRecordsOneRow()
    {
        var registered = await RegisterAsync(CustomersClient.NewCustomer(), CustomersTokens.Backoffice, NewKey());

        registered.Status.ShouldBe(201, registered.Body);
        (await RowAsync(registered)).ShouldBe(Row("backoffice", null, registered.CreatedId, 201));
    }

    [Fact]
    public async Task RefusalsAndReplaysRecordTheReturnedStatus()
    {
        var customer = CustomersClient.NewCustomer();
        var key = NewKey();
        var underage = await RegisterAsync(CustomersClient.NewCustomer() with { BirthDate = "2020-01-01" }, CustomersTokens.Backoffice, NewKey());
        var first = await RegisterAsync(customer, CustomersTokens.Backoffice, key);
        var replay = await RegisterAsync(customer, CustomersTokens.Backoffice, key);
        var duplicate = await RegisterAsync(customer with { Email = $"other.{Guid.NewGuid():N}@ledger.local" }, CustomersTokens.Backoffice, NewKey());

        (underage.Status, underage.ProblemType).ShouldBe((400, "validation-error"));
        (await RowAsync(underage)).ShouldBe(Row("backoffice", null, null, 400));
        first.Status.ShouldBe(201, first.Body);
        (replay.Status, replay.Replayed, replay.CreatedId).ShouldBe((201, true, first.CreatedId));
        (await RowAsync(first)).ShouldBe(Row("backoffice", null, first.CreatedId, 201));
        (await RowAsync(replay)).ShouldBe(Row("backoffice", null, first.CreatedId, 201));
        (duplicate.Status, duplicate.ProblemType).ShouldBe((409, "cpf-already-registered"));
        (await RowAsync(duplicate)).ShouldBe(Row("backoffice", null, null, 409));
    }

    [Fact]
    public async Task AuthenticationAndScopeRefusalsAreRecorded()
    {
        var ana = await environment.AnaAsync();

        var anonymous = await RegisterAsync(CustomersClient.NewCustomer(), token: null, NewKey());
        var forged = await RegisterAsync(
            CustomersClient.NewCustomer(), TestTokenIssuer.SignWithOtherKey(TestTokenIssuer.Service("backoffice", "customers.write")), NewKey());
        var delegated = await RegisterAsync(CustomersClient.NewCustomer(), CustomersTokens.Delegated(ana.Id), NewKey());

        (anonymous.Status, anonymous.ProblemType).ShouldBe((401, "authentication-required"));
        (await RowAsync(anonymous)).ShouldBe(Row(null, null, null, 401));
        (forged.Status, forged.ProblemType).ShouldBe((401, "authentication-required"));
        (await RowAsync(forged)).ShouldBe(Row(null, null, null, 401));
        (delegated.Status, delegated.ProblemType).ShouldBe((403, "insufficient-scope"));
        (await RowAsync(delegated)).ShouldBe(Row("channel-app", ana.Id, null, 403));
    }

    [Fact]
    public async Task FailedAuditUndoesTheRegistration()
    {
        var customer = CustomersClient.NewCustomer();
        var key = NewKey();
        var trace = AuditRows.NewTrace();
        var (index, _) = await environment.Vault.HmacAsync("customers-blind-index", Encoding.UTF8.GetBytes(customer.Cpf));

        TracedResponse refused;
        await using (await environment.Database.RefuseAuditInsertForTraceAsync("customers", trace))
        {
            refused = await RegisterAsync(customer, CustomersTokens.Backoffice, key, trace);
        }

        var withIndex = await environment.Database.CustomersWithIndexAsync(index);
        var storedKey = await environment.Database.RequestHashAsync("backoffice", Guid.Parse(key));
        var retried = await RegisterAsync(customer, CustomersTokens.Backoffice, key);

        (refused.Status, refused.ProblemType).ShouldBe((500, "internal-error"));
        withIndex.ShouldBe(0);
        storedKey.ShouldBeNull();
        (await environment.Database.AuditRowsAsync("customers", trace)).ShouldBeEmpty();
        (retried.Status, retried.Replayed).ShouldBe((201, false), retried.Body);
        (await RowAsync(retried)).ShouldBe(Row("backoffice", null, retried.CreatedId, 201));
    }

    [Fact]
    public async Task TrailHoldsNoPersonalData()
    {
        var customer = CustomersClient.NewCustomer() with { Name = "Iara Montes Quintela", BirthDate = "1969-12-03" };
        var key = NewKey();
        var formatted = $"{customer.Cpf[..3]}.{customer.Cpf[3..6]}.{customer.Cpf[6..9]}-{customer.Cpf[9..]}";

        var anonymous = await RegisterAsync(customer, token: null, NewKey());
        var invalid = await RegisterAsync(customer with { BirthDate = "2020-01-01" }, CustomersTokens.Backoffice, NewKey());
        var registered = await RegisterAsync(customer, CustomersTokens.Backoffice, key);
        var replay = await RegisterAsync(customer, CustomersTokens.Backoffice, key);
        var read = await environment.Client.SendTracedAsync(
            environment.Completions, HttpMethod.Get, $"/v1/customers/{registered.CreatedId}/personal-data", body: null, CustomersTokens.Compliance);
        var byCpf = await environment.Client.SendTracedAsync(
            environment.Completions, HttpMethod.Get, $"/v1/customers/{formatted}/personal-data", body: null, CustomersTokens.Compliance);

        new[] { anonymous.Status, invalid.Status, registered.Status, replay.Status, read.Status, byCpf.Status }
            .ShouldBe([401, 400, 201, 201, 200, 404]);
        (await environment.Database.SingleAuditRowAsync("customers", byCpf)).ResourceId.ShouldBeNull();
        var trail = await environment.Database.ColumnAsync(
            "postgres",
            "SELECT row_to_json(a)::text FROM customers.audit_events AS a UNION ALL SELECT row_to_json(a)::text FROM ledger.audit_events AS a");
        trail.Count.ShouldBeGreaterThanOrEqualTo(6);
        foreach (var row in trail)
        {
            foreach (var value in new[] { customer.Name, customer.Cpf, formatted, customer.BirthDate, customer.Email })
            {
                row.ShouldNotContain(value, Case.Insensitive);
            }
        }
    }

    private Task<TracedResponse> RegisterAsync(CustomerInput customer, string? token, string key, string? trace = null) =>
        environment.Client.SendTracedAsync(
            environment.Completions,
            HttpMethod.Post,
            "/v1/customers",
            new { name = customer.Name, cpf = customer.Cpf, birthDate = customer.BirthDate, email = customer.Email },
            token,
            key,
            trace: trace);

    // A única linha da requisição; o `trace_id` é conferido contra a resposta por `SingleAuditRowAsync`.
    private async Task<AuditRow> RowAsync(TracedResponse response) =>
        await environment.Database.SingleAuditRowAsync("customers", response) with { TraceId = TraceChecked };

    private static AuditRow Row(string? client, Guid? callerCustomer, Guid? resource, int result) =>
        new(client, callerCustomer, "register-customer", "customer", resource, result, TraceChecked);

    private static string NewKey() => Guid.NewGuid().ToString();
}
