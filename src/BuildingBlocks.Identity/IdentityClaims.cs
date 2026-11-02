namespace BuildingBlocks.Identity;

// Nomes de claim do contrato do token, sem o mapeamento para os nomes longos do .NET.
internal static class IdentityClaims
{
    internal const string AuthorizedParty = "azp";
    internal const string ClientId = "client_id";
    internal const string CustomerId = "customer_id";
    internal const string Scope = "scope";
}
