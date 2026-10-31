using System.Text.Json.Nodes;
using IntegrationTests.Identity;
using IntegrationTests.Infrastructure;
using IntegrationTests.Ledger;

namespace IntegrationTests.Hosts;

public sealed class OpenApiTests
{
    private const string DocumentRoute = "/openapi/v1.json";
    private const string ScalarRoute = "/scalar";

    // Rota -> escopo exigido e se ela exige `Idempotency-Key`.
    private static readonly (string Method, string Path, string Scope, bool Idempotent)[] LedgerOperations =
    [
        ("post", "/v1/accounts", "ledger.accounts.manage", true),
        ("put", "/v1/accounts/{id}/credit-limit", "ledger.accounts.manage", false),
        ("post", "/v1/accounts/{id}/credits", "ledger.credits.write", true),
        ("post", "/v1/accounts/{id}/debits", "ledger.debits.write", true),
        ("post", "/v1/accounts/{id}/entries/{entryId}/reversal", "ledger.entries.reverse", true),
    ];

    // Os `type` novos, por rota; `internal-error` vale para todas.
    private static readonly (string Method, string Path, string Type)[] NewProblemTypes =
    [
        ("post", "/v1/accounts", "customer-not-found"),
        ("post", "/v1/accounts/{id}/credits", "balance-limit-exceeded"),
        ("post", "/v1/accounts/{id}/entries/{entryId}/reversal", "balance-limit-exceeded"),
    ];

    private static readonly Dictionary<(string Method, string Path), string> Summaries = new()
    {
        [("post", "/v1/accounts")] = "Abre uma conta",
        [("put", "/v1/accounts/{id}/credit-limit")] = "Troca o limite de crédito",
        [("post", "/v1/accounts/{id}/credits")] = "Registra um crédito",
        [("post", "/v1/accounts/{id}/debits")] = "Registra um débito",
        [("post", "/v1/accounts/{id}/entries/{entryId}/reversal")] = "Estorna um lançamento",
    };

    [Fact]
    public async Task DocumentDescribesLedgerRoutesInDevelopment()
    {
        var document = await ServedDocumentAsync();
        var paths = document["paths"].ShouldNotBeNull().AsObject();

        paths.SelectMany(path => path.Value!.AsObject().Select(operation => (operation.Key, path.Key)))
            .Order()
            .ShouldBe(LedgerOperations.Select(operation => (operation.Method, operation.Path)).Order());
        foreach (var (method, path, scope, idempotent) in LedgerOperations)
        {
            var operation = paths[path]![method]!;
            var context = $"{method.ToUpperInvariant()} {path}";

            operation["security"]!.AsArray().ShouldHaveSingleItem()!["Bearer"]!.AsArray()
                .Select(value => value!.GetValue<string>()).ShouldBe([scope], context);
            var keyHeaders = (operation["parameters"]?.AsArray() ?? [])
                .Where(parameter => parameter!["in"]!.GetValue<string>() == "header" && parameter["name"]!.GetValue<string>() == "Idempotency-Key")
                .ToList();
            if (idempotent)
            {
                keyHeaders.ShouldHaveSingleItem(context)!["required"]!.GetValue<bool>().ShouldBeTrue(context);
            }
            else
            {
                keyHeaders.ShouldBeEmpty(context);
            }

            ProblemTypes(operation).ShouldContain("internal-error", context);
            ProblemTypes(operation).ShouldContain("authentication-required", context);
            ProblemTypes(operation).ShouldContain("insufficient-scope", context);
            operation["summary"]!.GetValue<string>().ShouldBe(Summaries[(method, path)], context);
            operation["description"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace(context);
        }

        foreach (var (method, path, type) in NewProblemTypes)
        {
            ProblemTypes(paths[path]![method]!).ShouldContain(type, $"{method.ToUpperInvariant()} {path}");
        }
    }

    [Fact]
    public async Task ScalarIsServedInDevelopment()
    {
        await using var api = IdentityApi.WithTestIssuer(environment: "Development", testRoutes: false);
        using var client = api.CreateClient();

        // O cliente segue o redirecionamento de `/scalar` para `/scalar/`, como um navegador.
        using var response = await client.GetAsync(ScalarRoute, token: null);

        ((int)response.StatusCode).ShouldBe(200);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("text/html");
    }

    [Theory]
    [InlineData(DocumentRoute)]
    [InlineData(ScalarRoute)]
    public async Task DocumentIsHiddenInProduction(string route)
    {
        await using var api = IdentityApi.WithTestIssuer(testRoutes: false);
        using var client = api.CreateClient();

        using var anonymous = await client.GetAsync(route, token: null);
        using var authenticated = await client.GetAsync(route, LedgerTokens.Backoffice);

        await anonymous.ShouldBeAuthenticationRequiredAsync();
        await authenticated.ShouldBeInsufficientScopeAsync();
    }

    [Fact]
    public async Task VersionedDocumentMatchesServed()
    {
        var served = await ServedDocumentAsync();
        var versioned = JsonNode.Parse(await File.ReadAllTextAsync(Repository.OpenApiDocument, TestContext.Current.CancellationToken));

        JsonNode.DeepEquals(served, versioned).ShouldBeTrue(
            $"{Repository.OpenApiDocument} differs from {DocumentRoute}; replace the file with the served document:\n{served.ToJsonString()}");
    }

    private static async Task<JsonNode> ServedDocumentAsync()
    {
        await using var api = IdentityApi.WithTestIssuer(environment: "Development", testRoutes: false);
        using var client = api.CreateClient();

        using var response = await client.GetAsync(DocumentRoute, token: null);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(200, text);
        var document = JsonNode.Parse(text).ShouldNotBeNull();
        document["openapi"]!.GetValue<string>().ShouldStartWith("3.1");
        return document;
    }

    // Os `type` de Problem Details de todas as respostas de erro da operação.
    private static List<string> ProblemTypes(JsonNode operation) =>
        operation["responses"]!.AsObject()
            .Select(response => response.Value!["content"]?["application/problem+json"]?["schema"]?["properties"]?["type"]?["enum"])
            .Where(types => types is not null)
            .SelectMany(types => types!.AsArray().Select(type => type!.GetValue<string>()))
            .ToList();
}
