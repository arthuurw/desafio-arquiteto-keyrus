using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace IntegrationTests.Customers;

[Collection(CustomersEnvironmentDefinition.Name)]
public sealed class PersonalDataAuditTests(CustomersEnvironment environment)
{
    [Fact]
    public async Task EveryAccessIsLoggedWithIdsOnly()
    {
        var customer = CustomersClient.NewCustomer() with { Name = "Davi Nunes Prado", BirthDate = "1979-03-08" };
        var id = await environment.Client.RegisteredAsync(customer);
        var missing = Guid.NewGuid();

        var found = await AuditOfAsync($"/v1/customers/{id}/personal-data", CustomersTokens.Compliance);
        var notFound = await AuditOfAsync($"/v1/customers/{missing}/personal-data", CustomersTokens.Compliance);
        var delegated = await AuditOfAsync($"/v1/customers/{id}/personal-data", CustomersTokens.Delegated(id));
        var masked = await AuditOfAsync($"/v1/customers/{id}", CustomersTokens.Backoffice);

        found.Status.ShouldBe(200);
        var foundLine = found.Lines.ShouldHaveSingleItem();
        foundLine.ShouldContain("read-personal-data");
        foundLine.ShouldContain("client compliance");
        foundLine.ShouldContain("customer_id -:");
        foundLine.ShouldContain($"customer {id:D}");
        foundLine.ShouldContain("result 200");
        // Sem `traceId` no corpo do `200`: o do log é o da requisição, filho do `traceparent` enviado.
        foundLine.ShouldContain($"traceId 00-{found.SentTrace}-");

        notFound.Status.ShouldBe(404);
        var notFoundLine = notFound.Lines.ShouldHaveSingleItem();
        notFoundLine.ShouldContain($"customer {missing:D}");
        notFoundLine.ShouldContain("result 404");
        notFound.BodyTraceId.ShouldNotBeNull().ShouldContain(notFound.SentTrace);
        notFoundLine.ShouldContain($"traceId {notFound.BodyTraceId}");

        delegated.Status.ShouldBe(200);
        var delegatedLine = delegated.Lines.ShouldHaveSingleItem();
        delegatedLine.ShouldContain("client channel-app");
        delegatedLine.ShouldContain($"customer_id {id:D}:");
        delegatedLine.ShouldContain("result 200");

        masked.Status.ShouldBe(200);
        masked.Lines.ShouldBeEmpty();

        string[] personal = [customer.Name, customer.Cpf, Formatted(customer.Cpf), customer.BirthDate, customer.Email];
        foreach (var line in found.Lines.Concat(notFound.Lines).Concat(delegated.Lines))
        {
            foreach (var value in personal)
            {
                line.ShouldNotContain(value);
            }
        }
    }

    [Fact]
    public async Task AccessEndingInInternalErrorIsLogged()
    {
        var customer = CustomersClient.NewCustomer() with { Name = "Elisa Moura Teles", BirthDate = "1983-11-21" };
        var id = await environment.Client.RegisteredAsync(customer);
        await environment.Vault.DestroyDekAsync(id);

        var failed = await AuditOfAsync($"/v1/customers/{id}/personal-data", CustomersTokens.Compliance);

        failed.Status.ShouldBe(500);
        var line = failed.Lines.ShouldHaveSingleItem();
        line.ShouldContain("read-personal-data");
        line.ShouldContain("client compliance");
        line.ShouldContain($"customer {id:D}");
        line.ShouldContain("result 500");
        failed.BodyTraceId.ShouldNotBeNull().ShouldContain(failed.SentTrace);
        line.ShouldContain($"traceId {failed.BodyTraceId}");
        foreach (var value in new[] { customer.Name, customer.Cpf, Formatted(customer.Cpf), customer.BirthDate, customer.Email })
        {
            line.ShouldNotContain(value);
        }
    }

    // Envia o `GET` com um `traceparent` novo; devolve o status, o `traceId` do corpo de erro e as linhas `Audit` logadas.
    private async Task<(int Status, string SentTrace, string? BodyTraceId, List<string> Lines)> AuditOfAsync(string route, string token)
    {
        var trace = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(route, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{trace}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}-01");
        var mark = environment.ApiLog.Mark();

        using var response = await environment.Client.SendAsync(request, TestContext.Current.CancellationToken);

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var bodyTrace = (int)response.StatusCode >= 400 ? JsonDocument.Parse(text).RootElement.GetProperty("traceId").GetString() : null;
        var lines = environment.ApiLog.Since(mark).Where(line => line.Category == "Audit").Select(line => line.Message).ToList();
        return ((int)response.StatusCode, trace, bodyTrace, lines);
    }

    private static string Formatted(string digits) => $"{digits[..3]}.{digits[3..6]}.{digits[6..9]}-{digits[9..]}";
}
