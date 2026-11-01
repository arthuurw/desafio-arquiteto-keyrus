using Customers.Domain;
using Customers.Infrastructure.Crypto;
using Customers.Infrastructure.Vault;

namespace Customers.Infrastructure.Persistence;

// O cliente decifrado, com os campos na forma em que foram cifrados: CPF só com dígitos, nascimento em `yyyy-MM-dd`.
internal sealed record CustomerData(Guid Id, string Name, string CpfDigits, string BirthDate, string Email, DateTimeOffset RegisteredAt);

// Cifra e decifra a linha do cliente campo a campo, cada um com a AAD do próprio campo (ADR-0016).
internal static class CustomerSeal
{
    internal static CustomerRow Seal(Customer customer, byte[] dek, KeyedHash cpfIndex) =>
        new(
            customer.Id,
            FieldCipher.Encrypt(dek, customer.Id, CustomerField.Name, customer.Name.Value),
            FieldCipher.Encrypt(dek, customer.Id, CustomerField.Cpf, customer.Cpf.Digits),
            FieldCipher.Encrypt(dek, customer.Id, CustomerField.BirthDate, customer.BirthDate.Iso),
            FieldCipher.Encrypt(dek, customer.Id, CustomerField.Email, customer.Email.Value),
            cpfIndex.Value,
            cpfIndex.Version);

    // Lança `CustomerDataIntegrityException` no primeiro campo que não decifrar.
    internal static CustomerData Open(CustomerRow row, byte[] dek) =>
        new(
            row.Id,
            FieldCipher.Decrypt(dek, row.Id, CustomerField.Name, row.Name),
            FieldCipher.Decrypt(dek, row.Id, CustomerField.Cpf, row.Cpf),
            FieldCipher.Decrypt(dek, row.Id, CustomerField.BirthDate, row.BirthDate),
            FieldCipher.Decrypt(dek, row.Id, CustomerField.Email, row.Email),
            row.RegisteredAt);
}
