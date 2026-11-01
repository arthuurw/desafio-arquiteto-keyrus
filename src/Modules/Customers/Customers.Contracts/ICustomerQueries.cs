namespace Customers.Contracts;

// Leitura de Customers para outros módulos, sem dado pessoal e sem expor as tabelas (ADR-0031).
public interface ICustomerQueries
{
    // Verdadeiro quando o cliente está cadastrado; responde sem depender do cofre de chaves.
    Task<bool> ExistsAsync(Guid customerId, CancellationToken cancellationToken);
}
