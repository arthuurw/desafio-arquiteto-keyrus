namespace Customers.Infrastructure.Persistence;

// Linha de `customers.customers`: só ciphertext e o blind index do CPF, nunca um dado pessoal em claro (ADR-0016).
internal sealed class CustomerRow
{
    internal CustomerRow(Guid id, byte[] name, byte[] cpf, byte[] birthDate, byte[] email, byte[] cpfBlindIndex, int cpfBlindIndexVersion)
    {
        Id = id;
        Name = name;
        Cpf = cpf;
        BirthDate = birthDate;
        Email = email;
        CpfBlindIndex = cpfBlindIndex;
        CpfBlindIndexVersion = cpfBlindIndexVersion;
    }

    internal Guid Id { get; private set; }

    internal byte[] Name { get; private set; }

    internal byte[] Cpf { get; private set; }

    internal byte[] BirthDate { get; private set; }

    internal byte[] Email { get; private set; }

    internal byte[] CpfBlindIndex { get; private set; }

    // Versão da chave do Transit que calculou o índice: após uma rotação, aponta as linhas a recalcular (ADR-0016).
    internal int CpfBlindIndexVersion { get; private set; }

    internal DateTimeOffset RegisteredAt { get; private set; }
}
