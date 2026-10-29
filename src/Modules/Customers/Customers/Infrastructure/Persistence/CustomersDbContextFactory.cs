using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Customers.Infrastructure.Persistence;

// Usada apenas pelo dotnet-ef para gerar migrações; não abre conexão.
internal sealed class CustomersDbContextFactory : IDesignTimeDbContextFactory<CustomersDbContext>
{
    public CustomersDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CustomersDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", CustomersDbContext.ConfigureNpgsql)
            .Options);
}
