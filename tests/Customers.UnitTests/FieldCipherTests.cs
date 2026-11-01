using System.Security.Cryptography;
using System.Text;
using Customers.Infrastructure.Crypto;

namespace Customers.UnitTests;

public sealed class FieldCipherTests
{
    private static readonly Guid CustomerA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid CustomerB = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly byte[] Dek = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void SameValueTwiceGivesDifferentCiphertext()
    {
        var first = FieldCipher.Encrypt(Dek, CustomerA, CustomerField.Cpf, "52998224725");
        var second = FieldCipher.Encrypt(Dek, CustomerA, CustomerField.Cpf, "52998224725");

        first.AsSpan(1, 12).SequenceEqual(second.AsSpan(1, 12)).ShouldBeFalse("nonce repeated");
        first.AsSpan(13).SequenceEqual(second.AsSpan(13)).ShouldBeFalse("ciphertext repeated");
        FieldCipher.Decrypt(Dek, CustomerA, CustomerField.Cpf, first).ShouldBe("52998224725");
        FieldCipher.Decrypt(Dek, CustomerA, CustomerField.Cpf, second).ShouldBe("52998224725");
    }

    [Fact]
    public void FormatIsVersionNonceCiphertextTag()
    {
        const string Plain = "Ana Maria Souza";
        var sealedField = FieldCipher.Encrypt(Dek, CustomerA, CustomerField.Name, Plain);
        var length = Encoding.UTF8.GetByteCount(Plain);

        sealedField.Length.ShouldBe(1 + 12 + length + 16);
        sealedField[0].ShouldBe((byte)0x01);

        // Decifrado aqui, pelo formato e pela AAD declarados, sem passar pelo FieldCipher.
        var plain = new byte[length];
        using var aes = new AesGcm(Dek, 16);
        aes.Decrypt(
            sealedField.AsSpan(1, 12),
            sealedField.AsSpan(13, length),
            sealedField.AsSpan(13 + length, 16),
            plain,
            Encoding.UTF8.GetBytes($"{CustomerA:D}|name"));
        Encoding.UTF8.GetString(plain).ShouldBe(Plain);
    }

    [Fact]
    public void AadBindsCustomerAndField()
    {
        var sealedField = FieldCipher.Encrypt(Dek, CustomerA, CustomerField.Email, "ana.souza@ledger.local");

        ShouldFailIntegrity(() => FieldCipher.Decrypt(Dek, CustomerB, CustomerField.Email, sealedField), CustomerB, CustomerField.Email);
        ShouldFailIntegrity(() => FieldCipher.Decrypt(Dek, CustomerA, CustomerField.Name, sealedField), CustomerA, CustomerField.Name);
        ShouldFailIntegrity(
            () => FieldCipher.Decrypt(RandomNumberGenerator.GetBytes(32), CustomerA, CustomerField.Email, sealedField),
            CustomerA,
            CustomerField.Email);

        // Qualquer byte alterado, inclusive no nonce e na tag.
        for (var index = 0; index < sealedField.Length; index++)
        {
            var tampered = (byte[])sealedField.Clone();
            tampered[index] ^= 0x01;
            ShouldFailIntegrity(() => FieldCipher.Decrypt(Dek, CustomerA, CustomerField.Email, tampered), CustomerA, CustomerField.Email);
        }

        var otherVersion = (byte[])sealedField.Clone();
        otherVersion[0] = 0x02;
        ShouldFailIntegrity(() => FieldCipher.Decrypt(Dek, CustomerA, CustomerField.Email, otherVersion), CustomerA, CustomerField.Email);
        var truncated = sealedField.AsSpan(0, 28).ToArray();
        ShouldFailIntegrity(() => FieldCipher.Decrypt(Dek, CustomerA, CustomerField.Email, truncated), CustomerA, CustomerField.Email);
    }

    private static void ShouldFailIntegrity(Action decrypt, Guid customerId, string field)
    {
        var failure = Should.Throw<CustomerDataIntegrityException>(decrypt);
        failure.CustomerId.ShouldBe(customerId);
        failure.Field.ShouldBe(field);
    }
}
