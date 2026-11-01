using System.Security.Cryptography;
using IntegrationTests.Identity;
using IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace IntegrationTests.Customers;

// Todo acesso aos dados pessoais completos deixa uma linha em `customers.audit_events`, e os dados só saem depois dela
// (ADR-0041, ADR-0042).
[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class PersonalDataAuditTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task SuccessfulReadIsRecorded()
    {
        var id = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());

        var compliance = await ReadAsync(id.ToString(), CustomersTokens.Compliance);
        var delegated = await ReadAsync(id.ToString(), CustomersTokens.Delegated(id));

        compliance.Status.ShouldBe(200, compliance.Body);
        (await RowAsync(compliance)).ShouldBe(Row("compliance", null, id, 200));
        delegated.Status.ShouldBe(200, delegated.Body);
        (await RowAsync(delegated)).ShouldBe(Row("channel-app", id, id, 200));
    }

    [Fact]
    public async Task EveryOutcomeIsRecorded()
    {
        var reader = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var other = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var tampered = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var badKey = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var missing = Guid.NewGuid();
        var stored = (await environment.Database.CustomerAsync(tampered)).ShouldNotBeNull();
        var cpf = (byte[])stored.Cpf.Clone();
        cpf[^1] ^= 0xFF;
        await environment.Database.OverwriteFieldAsync(tampered, "cpf", cpf);
        await environment.Vault.OverwriteDekAsync(badKey, RandomNumberGenerator.GetBytes(16));

        var notFound = await ReadAsync(missing.ToString(), CustomersTokens.Compliance);
        var invalidId = await ReadAsync("abc", CustomersTokens.Compliance);
        var otherCustomer = await ReadAsync(other.ToString(), CustomersTokens.Delegated(reader));
        var anonymous = await ReadAsync(reader.ToString(), token: null);
        var forged = await ReadAsync(reader.ToString(), TestTokenIssuer.SignWithOtherKey(TestTokenIssuer.Service("compliance", "customers.pii.read")));
        var withoutScope = await ReadAsync(reader.ToString(), CustomersTokens.Backoffice);
        var tamperedField = await ReadAsync(tampered.ToString(), CustomersTokens.Compliance);
        var malformedKey = await ReadAsync(badKey.ToString(), CustomersTokens.Compliance);

        (notFound.Status, notFound.ProblemType).ShouldBe((404, "customer-not-found"));
        (await RowAsync(notFound)).ShouldBe(Row("compliance", null, missing, 404));
        (invalidId.Status, invalidId.ProblemType).ShouldBe((404, "customer-not-found"));
        (await RowAsync(invalidId)).ShouldBe(Row("compliance", null, null, 404));
        (otherCustomer.Status, otherCustomer.ProblemType).ShouldBe((404, "customer-not-found"));
        (await RowAsync(otherCustomer)).ShouldBe(Row("channel-app", reader, other, 404));
        (anonymous.Status, anonymous.ProblemType).ShouldBe((401, "authentication-required"));
        (await RowAsync(anonymous)).ShouldBe(Row(null, null, reader, 401));
        (forged.Status, forged.ProblemType).ShouldBe((401, "authentication-required"));
        (await RowAsync(forged)).ShouldBe(Row(null, null, reader, 401));
        (withoutScope.Status, withoutScope.ProblemType).ShouldBe((403, "insufficient-scope"));
        (await RowAsync(withoutScope)).ShouldBe(Row("backoffice", null, reader, 403));
        (tamperedField.Status, tamperedField.ProblemType).ShouldBe((500, "internal-error"));
        (await RowAsync(tamperedField)).ShouldBe(Row("compliance", null, tampered, 500));
        (malformedKey.Status, malformedKey.ProblemType).ShouldBe((500, "internal-error"));
        (await RowAsync(malformedKey)).ShouldBe(Row("compliance", null, badKey, 500));
    }

    [Fact]
    public async Task AbandonedReadRecords499()
    {
        var id = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        await using var silent = new SilentVault();
        using var client = environment.CreateClient(new LogSink(), silent.Address);
        var trace = AuditRows.NewTrace();
        using var request = AuditRows.TracedRequest(
            HttpMethod.Get, $"/v1/customers/{id}/personal-data", json: null, CustomersTokens.Compliance, idempotencyKey: null, trace);
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // A Api espera o cofre que não responde; o cliente desiste enquanto ela espera.
        var sending = client.SendAsync(request, abandon.Token);
        await silent.FirstConnection.WaitAsync(TestContext.Current.CancellationToken);
        await abandon.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(sending);
        await environment.Completions.WaitAsync(trace);
        var row = (await environment.Database.AuditRowsAsync("customers", trace)).ShouldHaveSingleItem();
        (row with { TraceId = AuditRow.TraceChecked }).ShouldBe(Row("compliance", null, id, 499));
        row.TraceId.ShouldStartWith($"00-{trace}-");
    }

    [Fact]
    public async Task FailedAuditWithholdsTheData()
    {
        var customer = CustomersClient.NewCustomer() with { Name = "Helena Prado Vaz", BirthDate = "1975-02-28" };
        var id = await environment.Client.RegisteredAsync(customer);

        TracedResponse refused;
        await using (await environment.Database.RefuseAuditInsertAsync("customers", id))
        {
            refused = await ReadAsync(id.ToString(), CustomersTokens.Compliance);
        }

        (refused.Status, refused.ProblemType).ShouldBe((500, "internal-error"));
        foreach (var value in PersonalValues(customer))
        {
            refused.Body.ShouldNotContain(value);
        }

        (await environment.Database.AuditRowsAsync("customers", refused.Trace)).ShouldBeEmpty();
    }

    [Fact]
    public async Task NoAuditLogLine()
    {
        var id = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var mark = environment.ApiLog.Mark();

        var statuses = new[]
        {
            (await ReadAsync(id.ToString(), CustomersTokens.Compliance)).Status,
            (await ReadAsync(id.ToString(), CustomersTokens.Delegated(id))).Status,
            (await ReadAsync(Guid.NewGuid().ToString(), CustomersTokens.Compliance)).Status,
            (await ReadAsync("abc", CustomersTokens.Compliance)).Status,
            (await ReadAsync(id.ToString(), token: null)).Status,
            (await ReadAsync(id.ToString(), CustomersTokens.Backoffice)).Status,
        };

        statuses.ShouldBe([200, 200, 404, 404, 401, 403]);
        environment.ApiLog.Since(mark).Where(line => line.Category == "Audit").ShouldBeEmpty();
    }

    [Fact]
    public async Task FailedAuditKeepsTheRefusal()
    {
        var id = await environment.Client.RegisteredAsync(CustomersClient.NewCustomer());
        var missing = Guid.NewGuid();
        var mark = environment.ApiLog.Mark();

        TracedResponse anonymous, notFound, withoutScope;
        await using (await environment.Database.RefuseAuditInsertAsync("customers", id))
        await using (await environment.Database.RefuseAuditInsertAsync("customers", missing))
        {
            anonymous = await ReadAsync(id.ToString(), token: null);
            notFound = await ReadAsync(missing.ToString(), CustomersTokens.Compliance);
            withoutScope = await ReadAsync(id.ToString(), CustomersTokens.Backoffice);
        }

        var errors = environment.ApiLog.Since(mark).Where(line => line.Level == LogLevel.Error).ToList();

        (anonymous.Status, anonymous.ProblemType).ShouldBe((401, "authentication-required"));
        (notFound.Status, notFound.ProblemType).ShouldBe((404, "customer-not-found"));
        (withoutScope.Status, withoutScope.ProblemType).ShouldBe((403, "insufficient-scope"));
        foreach (var refusal in new[] { anonymous, notFound, withoutScope })
        {
            var line = errors.Where(error => error.Message.Contains(refusal.BodyTraceId!, StringComparison.Ordinal)).ShouldHaveSingleItem();
            line.Message.ShouldContain("read-personal-data");
            line.Message.ShouldContain($"{refusal.Status}");
            (await environment.Database.AuditRowsAsync("customers", refusal.Trace)).ShouldBeEmpty();
        }
    }

    private Task<TracedResponse> ReadAsync(string id, string? token) =>
        environment.Client.SendTracedAsync(environment.Completions, HttpMethod.Get, $"/v1/customers/{id}/personal-data", body: null, token);

    private Task<AuditRow> RowAsync(TracedResponse response) => environment.Database.CheckedAuditRowAsync("customers", response);

    private static AuditRow Row(string? client, Guid? callerCustomer, Guid? resource, int result) =>
        AuditRow.Expected(client, callerCustomer, "read-personal-data", "customer", resource, result);

    private static string[] PersonalValues(CustomerInput customer) =>
        [customer.Name, customer.Cpf, Formatted(customer.Cpf), customer.BirthDate, customer.Email];

    private static string Formatted(string digits) => $"{digits[..3]}.{digits[3..6]}.{digits[6..9]}-{digits[9..]}";
}
