using IntegrationTests.Ledger;

namespace IntegrationTests.Positions;

// Instantes da query em ISO 8601 com offset explícito: `Z` e `-03:00` são o mesmo instante; sem offset, recusado.
[Collection(PositionsApiDefinition.Name)]
public sealed class InstantFormatTests(PositionsEnvironment environment)
{
    private const string Utc = "2026-10-01T12:00:00Z";
    private const string BrasiliaOffset = "2026-10-01T09:00:00-03:00";

    public static TheoryData<string> Fields { get; } = ["at", "from", "to"];

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task InstantRequiresExplicitOffset(string field)
    {
        var account = await environment.OpenAccountAsync();
        await environment.Client.CreditOkAsync(account.Id, "1.00");

        var utc = await QueryAsync(account.Id, field, Utc);
        var offset = await QueryAsync(account.Id, field, BrasiliaOffset);
        var noOffset = await QueryAsync(account.Id, field, "2026-10-01T12:00:00");
        var word = await QueryAsync(account.Id, field, "ontem");

        utc.ShouldBeOk();
        offset.ShouldBe(utc);
        noOffset.ShouldBeValidationErrorOn(field);
        word.ShouldBeValidationErrorOn(field);
    }

    private Task<QueryResponse> QueryAsync(Guid accountId, string field, string value) => field switch
    {
        "at" => environment.Client.BalanceAsync(accountId, at: value),
        "from" => environment.Client.StatementAsync(accountId, from: value),
        _ => environment.Client.StatementAsync(accountId, to: value),
    };
}
