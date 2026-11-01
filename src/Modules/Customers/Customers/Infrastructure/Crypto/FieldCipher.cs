using System.Security.Cryptography;
using System.Text;

namespace Customers.Infrastructure.Crypto;

// Campos cifrados de `customers.customers` (ADR-0016): o nome vai na AAD, junto com o id do cliente.
internal static class CustomerField
{
    internal const string Name = "name";
    internal const string Cpf = "cpf";
    internal const string BirthDate = "birth_date";
    internal const string Email = "email";
}

// O campo não decifra com a DEK e a AAD dele: foi adulterado, ou copiado de outro cliente ou de outro campo.
internal sealed class CustomerDataIntegrityException(Guid customerId, string field, Exception? inner = null)
    : Exception($"Field '{field}' of customer {customerId:D} failed integrity verification.", inner)
{
    internal Guid CustomerId { get; } = customerId;

    internal string Field { get; } = field;
}

// AES-256-GCM campo a campo, no formato `0x01 | nonce (12) | ciphertext | tag (16)` (ADR-0016).
internal static class FieldCipher
{
    internal const byte FormatVersion = 0x01;
    internal const int NonceSize = 12;
    internal const int TagSize = 16;
    internal const int KeySize = 32;
    private const int HeaderSize = 1 + NonceSize;

    // Nonce aleatório por operação: o mesmo texto cifrado duas vezes nunca dá os mesmos bytes.
    internal static byte[] Encrypt(ReadOnlySpan<byte> dek, Guid customerId, string field, string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var sealedField = new byte[HeaderSize + plain.Length + TagSize];
        sealedField[0] = FormatVersion;
        var nonce = sealedField.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(dek, TagSize);
        aes.Encrypt(
            nonce,
            plain,
            sealedField.AsSpan(HeaderSize, plain.Length),
            sealedField.AsSpan(HeaderSize + plain.Length, TagSize),
            AssociatedData(customerId, field));
        CryptographicOperations.ZeroMemory(plain);
        return sealedField;
    }

    // Lança `CustomerDataIntegrityException` para qualquer campo que não decifre; nunca devolve texto parcial.
    internal static string Decrypt(ReadOnlySpan<byte> dek, Guid customerId, string field, ReadOnlySpan<byte> sealedField)
    {
        if (sealedField.Length < HeaderSize + TagSize || sealedField[0] != FormatVersion)
        {
            throw new CustomerDataIntegrityException(customerId, field);
        }

        var length = sealedField.Length - HeaderSize - TagSize;
        var plain = new byte[length];
        try
        {
            using var aes = new AesGcm(dek, TagSize);
            aes.Decrypt(
                sealedField.Slice(1, NonceSize),
                sealedField.Slice(HeaderSize, length),
                sealedField.Slice(HeaderSize + length, TagSize),
                plain,
                AssociatedData(customerId, field));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException exception)
        {
            throw new CustomerDataIntegrityException(customerId, field, exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    // `{customerId:D}|{campo}`: o ciphertext só decifra no cliente e no campo para os quais foi cifrado.
    internal static byte[] AssociatedData(Guid customerId, string field) => Encoding.UTF8.GetBytes($"{customerId:D}|{field}");
}
