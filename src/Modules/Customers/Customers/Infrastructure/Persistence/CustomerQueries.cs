using Customers.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Customers.Infrastructure.Persistence;

// Consulta de Customers para os outros módulos: só o PostgreSQL, sem o cofre, para o Ledger seguir sem ele (ADR-0016).
internal sealed class CustomerQueries(CustomersDbContext db) : ICustomerQueries
{
    public Task<bool> ExistsAsync(Guid customerId, CancellationToken cancellationToken) =>
        db.Customers.AnyAsync(row => row.Id == customerId, cancellationToken);
}
