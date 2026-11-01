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
    private static readonly (string Method, string Path, string Scope, bool Idempotent)[] Operations =
    [
        ("post", "/v1/accounts", "ledger.accounts.manage", true),
        ("put", "/v1/accounts/{id}/credit-limit", "ledger.accounts.manage", false),
        ("post", "/v1/accounts/{id}/credits", "ledger.credits.write", true),
        ("post", "/v1/accounts/{id}/debits", "ledger.debits.write", true),
        ("post", "/v1/accounts/{id}/entries/{entryId}/reversal", "ledger.entries.reverse", true),
        ("get", "/v1/accounts/{id}/balance", "positions.read", false),
        ("get", "/v1/accounts/{id}/statement", "positions.read", false),
        ("post", "/v1/customers", "customers.write", true),
        ("get", "/v1/customers/{id}", "customers.read", false),
        ("get", "/v1/customers/{id}/personal-data", "customers.pii.read", false),
    ];

    private static readonly string[] BadRequest = ["validation-error", "idempotency-key-missing", "idempotency-key-invalid"];

    // Contrato de erro de cada rota: status -> os `type` que ela pode responder. 401, 403 e 500 valem para todas.
    private static readonly Dictionary<(string Method, string Path), Dictionary<string, string[]>> Problems = new()
    {
        [("post", "/v1/accounts")] = WithCommon(new()
        {
            ["400"] = BadRequest,
            ["404"] = ["customer-not-found"],
            ["409"] = ["concurrency-timeout"],
            ["422"] = ["idempotency-key-reused"],
        }),
        [("put", "/v1/accounts/{id}/credit-limit")] = WithCommon(new()
        {
            ["400"] = ["validation-error"],
            ["404"] = ["account-not-found"],
            ["409"] = ["concurrency-timeout"],
            ["422"] = ["credit-limit-below-balance"],
        }),
        [("post", "/v1/accounts/{id}/credits")] = WithCommon(new()
        {
            ["400"] = BadRequest,
            ["404"] = ["account-not-found"],
            ["409"] = ["concurrency-timeout", "duplicate-external-reference"],
            ["422"] = ["balance-limit-exceeded", "idempotency-key-reused"],
        }),
        [("post", "/v1/accounts/{id}/debits")] = WithCommon(new()
        {
            ["400"] = BadRequest,
            ["404"] = ["account-not-found"],
            ["409"] = ["concurrency-timeout", "duplicate-external-reference"],
            ["422"] = ["insufficient-funds", "idempotency-key-reused"],
        }),
        [("post", "/v1/accounts/{id}/entries/{entryId}/reversal")] = WithCommon(new()
        {
            ["400"] = BadRequest,
            ["404"] = ["account-not-found", "entry-not-found"],
            ["409"] = ["concurrency-timeout", "entry-already-reversed", "duplicate-external-reference"],
            ["422"] = ["entry-is-reversal", "insufficient-funds", "balance-limit-exceeded", "idempotency-key-reused"],
        }),
        [("get", "/v1/accounts/{id}/balance")] = WithCommon(new()
        {
            ["400"] = ["validation-error"],
            ["404"] = ["account-not-found"],
        }),
        [("get", "/v1/accounts/{id}/statement")] = WithCommon(new()
        {
            ["400"] = ["validation-error"],
            ["404"] = ["account-not-found"],
        }),
        [("post", "/v1/customers")] = WithCommon(new()
        {
            ["400"] = BadRequest,
            ["409"] = ["cpf-already-registered"],
            ["422"] = ["idempotency-key-reused"],
            ["503"] = ["personal-data-unavailable"],
        }),
        [("get", "/v1/customers/{id}")] = WithCommon(new()
        {
            ["404"] = ["customer-not-found"],
            ["503"] = ["personal-data-unavailable"],
        }),
        [("get", "/v1/customers/{id}/personal-data")] = WithCommon(new()
        {
            ["404"] = ["customer-not-found"],
            ["503"] = ["personal-data-unavailable"],
        }),
    };

    private static readonly Dictionary<(string Method, string Path), string> Summaries = new()
    {
        [("post", "/v1/accounts")] = "Abre uma conta",
        [("put", "/v1/accounts/{id}/credit-limit")] = "Troca o limite de crédito",
        [("post", "/v1/accounts/{id}/credits")] = "Registra um crédito",
        [("post", "/v1/accounts/{id}/debits")] = "Registra um débito",
        [("post", "/v1/accounts/{id}/entries/{entryId}/reversal")] = "Estorna um lançamento",
        [("get", "/v1/accounts/{id}/balance")] = "Consulta o saldo",
        [("get", "/v1/accounts/{id}/statement")] = "Consulta o extrato",
        [("post", "/v1/customers")] = "Cadastra um cliente",
        [("get", "/v1/customers/{id}")] = "Consulta um cliente, mascarado",
        [("get", "/v1/customers/{id}/personal-data")] = "Consulta os dados pessoais completos de um cliente",
    };

    [Fact]
    public async Task DocumentDescribesEveryRouteInDevelopment()
    {
        var document = await ServedDocumentAsync();
        var paths = document["paths"].ShouldNotBeNull().AsObject();

        paths.SelectMany(path => path.Value!.AsObject().Select(operation => (operation.Key, path.Key)))
            .Order()
            .ShouldBe(Operations.Select(operation => (operation.Method, operation.Path)).Order());
        foreach (var (method, path, scope, idempotent) in Operations)
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

            var problems = ProblemTypes(operation);
            problems.Keys.Order().ShouldBe(Problems[(method, path)].Keys.Order(), context);
            foreach (var (status, types) in Problems[(method, path)])
            {
                problems[status].Order().ShouldBe(types.Order(), $"{context} {status}");
            }

            operation["summary"]!.GetValue<string>().ShouldBe(Summaries[(method, path)], context);
            operation["description"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace(context);
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

    // Os `type` de Problem Details de cada status de erro da operação.
    private static Dictionary<string, List<string>> ProblemTypes(JsonNode operation) =>
        operation["responses"]!.AsObject()
            .Select(response => (Status: response.Key, Types: response.Value!["content"]?["application/problem+json"]?["schema"]?["properties"]?["type"]?["enum"]))
            .Where(response => response.Types is not null)
            .ToDictionary(response => response.Status, response => response.Types!.AsArray().Select(type => type!.GetValue<string>()).ToList());

    private static Dictionary<string, string[]> WithCommon(Dictionary<string, string[]> problems)
    {
        problems["401"] = ["authentication-required"];
        problems["403"] = ["insufficient-scope"];
        problems["500"] = ["internal-error"];
        return problems;
    }
}
