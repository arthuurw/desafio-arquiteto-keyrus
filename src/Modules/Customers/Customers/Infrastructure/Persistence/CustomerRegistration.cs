using System.Security.Cryptography;
using Customers.Domain;
using Customers.Infrastructure.Crypto;
using Customers.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Customers.Infrastructure.Persistence;

internal enum RegistrationOutcome
{
    Registered,
    CpfAlreadyRegistered,
}

// Cadastro de um cliente: blind index, DEK nova no cofre, os quatro campos cifrados e a linha (ADR-0016). A DEK vai
// para o cofre antes da linha: uma falha entre os dois deixa só uma DEK órfã, nunca uma linha sem chave.
internal sealed class CustomerRegistration(CustomersDbContext db, IBlindIndexer indexer, IKeyStore keys)
{
    internal Task<RegistrationOutcome> RegisterAsync(Customer customer, CancellationToken cancellationToken) =>
        RegisterAsync(customer, keptDek: null, cancellationToken);

    // `keptDek`: a DEK que o cliente já tem no cofre, usada no lugar de uma nova (o `cas=0` recusaria gravar outra).
    // Quem a leu do cofre a zera; a nova, o registro zera no fim.
    internal async Task<RegistrationOutcome> RegisterAsync(Customer customer, byte[]? keptDek, CancellationToken cancellationToken)
    {
        var cpfIndex = await indexer.CpfAsync(customer.Cpf.Digits, cancellationToken);
        // A consulta recusa o caso comum sem gastar uma DEK; o índice único recusa a corrida, que ela não vê.
        if (await db.Customers.AnyAsync(row => row.CpfBlindIndex == cpfIndex.Value, cancellationToken))
        {
            return RegistrationOutcome.CpfAlreadyRegistered;
        }

        var dek = keptDek ?? RandomNumberGenerator.GetBytes(FieldCipher.KeySize);
        try
        {
            if (keptDek is null)
            {
                await keys.CreateAsync(customer.Id, dek, cancellationToken);
            }

            db.Customers.Add(CustomerSeal.Seal(customer, dek, cpfIndex));
            await db.SaveChangesAsync(cancellationToken);
            return RegistrationOutcome.Registered;
        }
        catch (DbUpdateException exception) when (IsDuplicateCpf(exception))
        {
            // O savepoint do EF desfez só o INSERT: a transação segue, e a recusa é guardada com a chave.
            db.ChangeTracker.Clear();
            return RegistrationOutcome.CpfAlreadyRegistered;
        }
        finally
        {
            if (keptDek is null)
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
    }

    private static bool IsDuplicateCpf(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && postgres.ConstraintName == CustomersDbContext.CpfBlindIndexUnique;
}
