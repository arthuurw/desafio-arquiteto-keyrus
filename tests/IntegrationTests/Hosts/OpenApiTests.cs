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
            ["413"] = ["payload-too-large"],
            ["503"] = ["database-timeout"],
        }),
        [("put", "/v1/accounts/{id}/credit-limit")] = WithCommon(new()
        {
            ["400"] = ["validation-error"],
            ["404"] = ["account-not-found"],
            ["409"] = ["concurrency-timeout"],
            ["422"] = ["credit-limit-below-balance"],
            ["413"] = ["payload-too-large"],
            ["503"] = ["database-timeout"],
        }),
        [("post", "/v1/accounts/{id}/credits")] = WithCommon(new()
        {
            ["400"] = BadRequest,
            ["404"] = ["account-not-found"],
            ["409"] = ["concurrency-timeout", "duplicate-external-reference"],
            ["422"] = ["balance-limit-exceeded", "idempotency-key-reused"],
            ["413"] = ["payload-too-large"],
            ["503"] = ["database-timeout"],
        }),
        [("post", "/v1/accounts/{id}/debits")] = WithCommon(new()
        {
            ["400"] = BadRequest,
            ["404"] = ["account-not-found"],
            ["409"] = ["concurrency-timeout", "duplicate-external-reference"],
            ["422"] = ["insufficient-funds", "idempotency-key-reused"],
            ["413"] = ["payload-too-large"],
            ["503"] = ["database-timeout"],
        }),
        [("post", "/v1/accounts/{id}/entries/{entryId}/reversal")] = WithCommon(new()
        {
            ["400"] = BadRequest,
            ["404"] = ["account-not-found", "entry-not-found"],
            ["409"] = ["concurrency-timeout", "entry-already-reversed", "duplicate-external-reference"],
            ["422"] = ["entry-is-reversal", "insufficient-funds", "balance-limit-exceeded", "idempotency-key-reused"],
            ["413"] = ["payload-too-large"],
            ["503"] = ["database-timeout"],
        }),
        [("get", "/v1/accounts/{id}/balance")] = WithCommon(new()
        {
            ["400"] = ["validation-error"],
            ["404"] = ["account-not-found"],
            ["409"] = ["concurrency-timeout"],
        }),
        [("get", "/v1/accounts/{id}/statement")] = WithCommon(new()
        {
            ["400"] = ["validation-error"],
            ["404"] = ["account-not-found"],
            ["409"] = ["concurrency-timeout"],
        }),
        [("post", "/v1/customers")] = WithCommon(new()
        {
            ["400"] = BadRequest,
            ["409"] = ["cpf-already-registered"],
            ["422"] = ["idempotency-key-reused"],
            ["503"] = ["personal-data-unavailable"],
            ["413"] = ["payload-too-large"],
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

    // As leituras com instante podem esperar a escrita e recusar com `409`; toda rota com corpo, e só ela, pode recusar o
    // corpo grande com `413`.
    [Fact]
    public async Task ReadsDeclareConcurrencyTimeoutAndBodiesDeclarePayloadTooLarge()
    {
        var document = await ServedDocumentAsync();
        var operations = document["paths"].ShouldNotBeNull().AsObject()
            .SelectMany(path => path.Value!.AsObject().Select(operation => (Method: operation.Key, Path: path.Key, Operation: operation.Value!)))
            .ToList();

        foreach (var path in new[] { "/v1/accounts/{id}/balance", "/v1/accounts/{id}/statement" })
        {
            ProblemTypes(operations.Single(operation => operation.Method == "get" && operation.Path == path).Operation)["409"]
                .ShouldBe(["concurrency-timeout"], path);
        }

        var withBody = operations.Where(operation => operation.Operation["requestBody"] is not null).ToList();
        withBody.Select(operation => (operation.Method, operation.Path)).Order().ShouldBe(
        [
            ("post", "/v1/accounts"),
            ("post", "/v1/accounts/{id}/credits"),
            ("post", "/v1/accounts/{id}/debits"),
            ("post", "/v1/accounts/{id}/entries/{entryId}/reversal"),
            ("post", "/v1/customers"),
            ("put", "/v1/accounts/{id}/credit-limit"),
        ]);
        foreach (var (method, path, operation) in operations)
        {
            List<string> declared = ProblemTypes(operation).TryGetValue("413", out var types) ? types : [];
            List<string> expected = operation["requestBody"] is null ? [] : ["payload-too-large"];
            declared.ShouldBe(expected, $"{method} {path}");
        }
    }

    [Fact]
    public async Task StatementDescribesFutureToRefusal()
    {
        var document = await ServedDocumentAsync();

        var description = document["paths"]!["/v1/accounts/{id}/statement"]!["get"]!["description"]!.GetValue<string>();

        description.ShouldContain("`to` no futuro é recusado");
    }

    // A chave guarda o sucesso e a recusa de negócio; a repetição devolve o mesmo status com o header.
    [Fact]
    public async Task IdempotentRoutesDeclareReplayHeader()
    {
        var paths = (await ServedDocumentAsync())["paths"]!;
        string[] replayable = ["201", "404", "409", "422"];

        foreach (var (method, path, _, idempotent) in Operations)
        {
            var responses = paths[path]![method]!["responses"]!.AsObject();
            var declaring = responses
                .Where(response => response.Value!["headers"]?["Idempotent-Replayed"] is not null)
                .Select(response => response.Key)
                .Order(StringComparer.Ordinal)
                .ToList();
            var context = $"{method.ToUpperInvariant()} {path}";

            if (!idempotent)
            {
                declaring.ShouldBeEmpty(context);
                continue;
            }

            responses.ShouldContainKey("201", context);
            declaring.ShouldBe(responses.Select(response => response.Key).Where(replayable.Contains).Order(StringComparer.Ordinal).ToList(), context);
            foreach (var status in declaring)
            {
                var header = responses[status]!["headers"]!["Idempotent-Replayed"]!;
                (header["required"]?.GetValue<bool>() ?? false).ShouldBeFalse($"{context} {status}");
                header["schema"]!["type"]!.GetValue<string>().ShouldBe("string", $"{context} {status}");
                header["schema"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()).ShouldBe(["true"], $"{context} {status}");
            }
        }
    }

    [Fact]
    public async Task UnavailableDeclaresRetryAfter()
    {
        var paths = (await ServedDocumentAsync())["paths"]!.AsObject();

        var declaring = paths
            .SelectMany(path => path.Value!.AsObject().SelectMany(operation => operation.Value!["responses"]!.AsObject()
                .Where(response => response.Value!["headers"]?["Retry-After"] is not null)
                .Select(response => (Path: path.Key, Method: operation.Key, Status: response.Key, Header: response.Value!["headers"]!["Retry-After"]!))))
            .ToList();

        declaring.Select(item => $"{item.Method} {item.Path} {item.Status}").Order(StringComparer.Ordinal).ShouldBe(
        [
            "get /v1/customers/{id} 503",
            "get /v1/customers/{id}/personal-data 503",
            "post /v1/accounts 503",
            "post /v1/accounts/{id}/credits 503",
            "post /v1/accounts/{id}/debits 503",
            "post /v1/accounts/{id}/entries/{entryId}/reversal 503",
            "post /v1/customers 503",
            "put /v1/accounts/{id}/credit-limit 503",
        ]);
        declaring.ShouldAllBe(item => item.Header["schema"]!["type"]!.GetValue<string>() == "integer");
    }

    [Fact]
    public async Task DuplicateReferenceDeclaresIds()
    {
        var document = await ServedDocumentAsync();
        string[] withIds = ["/v1/accounts/{id}/credits", "/v1/accounts/{id}/debits", "/v1/accounts/{id}/entries/{entryId}/reversal"];

        foreach (var (method, path, _, _) in Operations)
        {
            var schema = document["paths"]![path]![method]!["responses"]!["409"]?["content"]?["application/problem+json"]?["schema"];
            if (!withIds.Contains(path))
            {
                (schema?["properties"]?["entryId"]).ShouldBeNull(path);
                (schema?["properties"]?["accountId"]).ShouldBeNull(path);
                continue;
            }

            var required = schema!["required"]!.AsArray().Select(value => value!.GetValue<string>()).ToList();
            foreach (var name in new[] { "entryId", "accountId" })
            {
                schema["properties"]![name]!["type"]!.GetValue<string>().ShouldBe("string", $"{path} {name}");
                schema["properties"]![name]!["format"]!.GetValue<string>().ShouldBe("uuid", $"{path} {name}");
                required.ShouldNotContain(name, $"{path} {name}");
            }
        }
    }

    [Fact]
    public async Task LongsAreIntegers()
    {
        var schemas = (await ServedDocumentAsync())["components"]!["schemas"]!.AsObject();

        var found = schemas
            .SelectMany(schema => (schema.Value!["properties"]?.AsObject() ?? [])
                .Where(property => property.Key is "version" or "sequence")
                .Select(property => (Name: $"{schema.Key}.{property.Key}", property.Value)))
            .ToList();

        var names = found.Select(property => property.Name).ToList();
        foreach (var expected in new[] { "BalanceResponse.version", "EntryResponse.version", "EntryResponse.sequence", "StatementItem.sequence" })
        {
            names.ShouldContain(expected);
        }
        foreach (var (name, value) in found)
        {
            JsonNode.DeepEquals(value, JsonNode.Parse("""{"type": "integer", "format": "int64"}""")).ShouldBeTrue($"{name}: {value!.ToJsonString()}");
        }
    }

    [Fact]
    public async Task StatementLimitHasRange()
    {
        var parameters = (await ServedDocumentAsync())["paths"]!["/v1/accounts/{id}/statement"]!["get"]!["parameters"]!.AsArray();

        var limit = parameters.Single(parameter => parameter!["name"]!.GetValue<string>() == "limit")!["schema"]!;

        limit["type"]!.GetValue<string>().ShouldBe("integer");
        limit["minimum"]!.GetValue<int>().ShouldBe(1);
        limit["maximum"]!.GetValue<int>().ShouldBe(200);
        limit["default"]!.GetValue<int>().ShouldBe(50);
    }

    [Fact]
    public async Task EntryRequestsDeclareTheirCategories()
    {
        var document = await ServedDocumentAsync();
        var schemas = document["components"]!["schemas"]!.AsObject();

        BodySchema(document, "/v1/accounts/{id}/credits").ShouldBe("#/components/schemas/CreditRequest");
        BodySchema(document, "/v1/accounts/{id}/debits").ShouldBe("#/components/schemas/DebitRequest");
        Categories(schemas, "CreditRequest").ShouldBe(["PIX_IN", "TED_IN", "BOLETO_IN", "DEPOSIT", "ADJUSTMENT_IN"]);
        Categories(schemas, "DebitRequest").ShouldBe(["PIX_OUT", "TED_OUT", "BOLETO_OUT", "CARD", "FEE", "ADJUSTMENT_OUT"]);
        schemas.ContainsKey("EntryRequest").ShouldBeFalse();
    }

    [Fact]
    public async Task MoneyAndReferenceDeclarePatterns()
    {
        var schemas = (await ServedDocumentAsync())["components"]!["schemas"]!;
        const string money = @"^[0-9]{1,12}(\.[0-9]{1,2})?$";
        const string reference = "^[A-Za-z0-9._:-]{1,64}$";

        foreach (var (schema, property, pattern) in new[]
        {
            ("CreditRequest", "amount", money),
            ("DebitRequest", "amount", money),
            ("CreditLimitRequest", "creditLimit", money),
            ("CreditRequest", "externalReference", reference),
            ("DebitRequest", "externalReference", reference),
            ("ReversalRequest", "externalReference", reference),
        })
        {
            schemas[schema]!["properties"]![property]!["pattern"]!.GetValue<string>().ShouldBe(pattern, $"{schema}.{property}");
        }
    }

    [Fact]
    public async Task InstantsDeclarePattern()
    {
        var paths = (await ServedDocumentAsync())["paths"]!;
        const string instant = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})$";

        foreach (var (path, name) in new[]
        {
            ("/v1/accounts/{id}/balance", "at"),
            ("/v1/accounts/{id}/statement", "from"),
            ("/v1/accounts/{id}/statement", "to"),
        })
        {
            var parameter = paths[path]!["get"]!["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == name)!;
            parameter["schema"]!["pattern"]!.GetValue<string>().ShouldBe(instant, name);
            parameter["description"]!.GetValue<string>().ShouldContain("`T` e `Z` maiúsculos, até 7 casas decimais", Case.Sensitive, name);
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

    private static string BodySchema(JsonNode document, string path) =>
        document["paths"]![path]!["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>();

    private static List<string> Categories(JsonObject schemas, string schema) =>
        schemas[schema]!["properties"]!["category"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()).ToList();

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
