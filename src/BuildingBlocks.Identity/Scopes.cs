namespace BuildingBlocks.Identity;

// Escopos do ADR-0013; cada rota exige exatamente um deles.
public static class Scopes
{
    public const string LedgerCreditsWrite = "ledger.credits.write";
    public const string LedgerDebitsWrite = "ledger.debits.write";
    public const string LedgerEntriesReverse = "ledger.entries.reverse";
    public const string LedgerAccountsManage = "ledger.accounts.manage";
    public const string PositionsRead = "positions.read";
    public const string CustomersWrite = "customers.write";
    public const string CustomersRead = "customers.read";
    public const string CustomersPiiRead = "customers.pii.read";
    public const string CustomersErasureManage = "customers.erasure.manage";

    public static IReadOnlyList<string> All { get; } =
    [
        LedgerCreditsWrite,
        LedgerDebitsWrite,
        LedgerEntriesReverse,
        LedgerAccountsManage,
        PositionsRead,
        CustomersWrite,
        CustomersRead,
        CustomersPiiRead,
        CustomersErasureManage,
    ];
}
