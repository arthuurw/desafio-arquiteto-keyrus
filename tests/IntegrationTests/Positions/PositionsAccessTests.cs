using IntegrationTests.Ledger;

namespace IntegrationTests.Positions;

// Escopo `positions.read` e titularidade nas duas rotas (ADR-0013).
[Collection(PositionsApiDefinition.Name)]
public sealed class PositionsAccessTests(PositionsEnvironment environment)
{
    public static TheoryData<string> Routes { get; } = ["balance", "statement"];

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task UnknownOrForeignAccountIsNotFound(string route)
    {
        var other = await environment.OpenAccountAsync();
        var caller = await environment.OpenAccountAsync();

        var responses = new[]
        {
            await environment.Client.QueryAsync($"/v1/accounts/{Guid.NewGuid()}/{route}", LedgerTokens.Backoffice),
            await environment.Client.QueryAsync($"/v1/accounts/nao-e-uuid/{route}", LedgerTokens.Backoffice),
            await environment.Client.QueryAsync($"/v1/accounts/{other.Id}/{route}", LedgerTokens.Channel(caller.CustomerId)),
        };

        foreach (var response in responses)
        {
            response.Status.ShouldBe(404, response.Text);
            var body = response.Body;
            body["type"]!.GetValue<string>().ShouldBe("account-not-found");
            body["title"]!.GetValue<string>().ShouldBe("Conta não encontrada");
            body["detail"]!.GetValue<string>().ShouldBe("A conta informada não existe.");
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task ScopeAndModeDecideAccess(string route)
    {
        var account = await environment.OpenAccountAsync();
        var path = $"/v1/accounts/{account.Id}/{route}";

        var backoffice = await environment.Client.QueryAsync(path, LedgerTokens.Backoffice);
        var compliance = await environment.Client.QueryAsync(path, PositionsClient.Compliance);
        var owner = await environment.Client.QueryAsync(path, LedgerTokens.Channel(account.CustomerId));
        var paymentsHub = await environment.Client.QueryAsync(path, LedgerTokens.PaymentsHub);
        var anonymous = await environment.Client.QueryAsync(path, token: null);

        backoffice.Status.ShouldBe(200, backoffice.Text);
        compliance.Status.ShouldBe(200, compliance.Text);
        owner.Status.ShouldBe(200, owner.Text);
        backoffice.Body["accountId"]!.GetValue<Guid>().ShouldBe(account.Id);
        owner.Body["accountId"]!.GetValue<Guid>().ShouldBe(account.Id);
        paymentsHub.Status.ShouldBe(403, paymentsHub.Text);
        paymentsHub.Body["type"]!.GetValue<string>().ShouldBe("insufficient-scope");
        anonymous.Status.ShouldBe(401, anonymous.Text);
        anonymous.Body["type"]!.GetValue<string>().ShouldBe("authentication-required");
    }

    [Fact]
    public async Task FutureToFollowsAccess()
    {
        var account = await environment.OpenAccountAsync();
        var other = await environment.OpenAccountAsync();
        var now = await environment.Database.ScalarAsync<DateTime>("postgres", "SELECT clock_timestamp()");
        var to = PositionsClient.Iso(now.AddDays(1));

        var owner = await environment.Client.StatementAsync(account.Id, to: to, token: LedgerTokens.Channel(account.CustomerId));
        var service = await environment.Client.StatementAsync(account.Id, to: to, token: LedgerTokens.Backoffice);
        var foreign = await environment.Client.StatementAsync(account.Id, to: to, token: LedgerTokens.Channel(other.CustomerId));

        owner.ShouldBeValidationErrorOn("to");
        service.ShouldBeValidationErrorOn("to");
        foreign.Status.ShouldBe(404, foreign.Text);
        foreign.Body["type"]!.GetValue<string>().ShouldBe("account-not-found");
    }
}
