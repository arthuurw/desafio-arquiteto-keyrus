using static IntegrationTests.Identity.TestTokenIssuer;

namespace IntegrationTests.Ledger;

// Tokens com os escopos que cada client recebe no realm local; assinados a cada uso, pois expiram em 300 s.
internal static class LedgerTokens
{
    internal static string Backoffice =>
        Sign(Service("backoffice", "ledger.accounts.manage ledger.entries.reverse customers.write customers.read positions.read"));

    internal static string PaymentsHub => Sign(Service("payments-hub", "ledger.credits.write"));

    // Canal no modo delegado, em nome do cliente logado.
    internal static string Channel(Guid customerId) =>
        Sign(Delegated(customerId.ToString(), "ledger.debits.write positions.read customers.read"));
}
