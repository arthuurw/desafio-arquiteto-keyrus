namespace Customers.Domain;

// Cliente com os dados pessoais já validados e normalizados, em claro: existe só na memória da requisição. O que vai
// para o banco é a versão cifrada, campo a campo (ADR-0016).
internal sealed record Customer(Guid Id, CustomerName Name, Cpf Cpf, BirthDate BirthDate, Email Email)
{
    internal static Customer Register(CustomerName name, Cpf cpf, BirthDate birthDate, Email email) =>
        new(Guid.CreateVersion7(), name, cpf, birthDate, email);
}
