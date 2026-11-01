using System.Text.Json;

namespace IntegrationTests.Ledger;

// Contrato das respostas do Ledger: lançamento em `201` e erros em Problem Details (ADR-0042).
internal static class LedgerAssertions
{
    internal static async Task<JsonElement> ShouldBeEntryAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(201, text);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/json");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    internal static async Task<JsonElement> ShouldBeProblemAsync(this HttpResponseMessage response, int status, string type)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(status, text);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");
        var body = JsonDocument.Parse(text).RootElement.Clone();
        body.GetProperty("type").GetString().ShouldBe(type);
        body.GetProperty("status").GetInt32().ShouldBe(status);
        body.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        body.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
        body.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        return body;
    }

    internal static async Task ShouldBeValidationErrorAsync(this HttpResponseMessage response, string field)
    {
        var body = await response.ShouldBeProblemAsync(400, "validation-error");
        var errors = body.GetProperty("errors");

        errors.TryGetProperty(field, out var messages).ShouldBeTrue($"errors has no '{field}': {errors}");
        messages.GetArrayLength().ShouldBeGreaterThan(0);
    }
}
