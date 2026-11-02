using BuildingBlocks.Audit;
using Customers.Infrastructure.Persistence;

namespace Customers.Features;

// Trilha de auditoria de Customers, gravada pela `customers_app` (ADR-0041).
internal static class CustomersAudit
{
    internal const string RegisterCustomer = "register-customer";
    internal const string ReadPersonalData = "read-personal-data";

    internal const string Customer = "customer";

    internal static AuditTrail Trail { get; } = new("customers.audit_events", typeof(CustomersDbContext));
}
