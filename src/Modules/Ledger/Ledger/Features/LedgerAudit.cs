using BuildingBlocks.Audit;
using Ledger.Infrastructure.Persistence;

namespace Ledger.Features;

// Trilha de auditoria do Ledger, gravada pela `ledger_app` (ADR-0041): o nome de cada operação e de cada recurso.
internal static class LedgerAudit
{
    internal const string OpenAccount = "open-account";
    internal const string ChangeCreditLimit = "change-credit-limit";
    internal const string Credit = "credit";
    internal const string Debit = "debit";
    internal const string ReverseEntry = "reverse-entry";

    internal const string Account = "account";
    internal const string Entry = "entry";

    internal static AuditTrail Trail { get; } = new("ledger.audit_events", typeof(LedgerDbContext));
}
