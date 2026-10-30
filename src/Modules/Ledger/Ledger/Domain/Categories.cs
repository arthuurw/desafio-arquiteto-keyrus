namespace Ledger.Domain;

// Catálogo fechado por tipo: lançamento não tem texto livre (ADR-0017).
internal static class Categories
{
    // Atribuída só pela Api ao estorno; nenhum chamador a envia.
    internal const string Reversal = "REVERSAL";

    internal static IReadOnlyList<string> Credit => ["PIX_IN", "TED_IN", "BOLETO_IN", "DEPOSIT", "ADJUSTMENT_IN"];

    internal static IReadOnlyList<string> Debit => ["PIX_OUT", "TED_OUT", "BOLETO_OUT", "CARD", "FEE", "ADJUSTMENT_OUT"];

    // Comparação exata: `pix_in` não é `PIX_IN`.
    internal static bool Accepts(EntryType type, string category) =>
        (type == EntryType.Credit ? Credit : Debit).Contains(category, StringComparer.Ordinal);
}
