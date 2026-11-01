using Customers.Domain;

namespace Customers.UnitTests;

public sealed class CustomerMaskTests
{
    [Theory]
    [InlineData("Ana Maria Souza", "Ana M. S.")]
    [InlineData("Ana", "Ana")]
    [InlineData("Ana   Maria", "Ana M.")] // `  Ana   Maria  ` depois de aparado no cadastro
    [InlineData("Ana 𝓜aria Souza", "Ana 𝓜. S.")]
    [InlineData("Ana Érica", "Ana É.")]
    public void NameKeepsTheFirstWordAndInitials(string name, string masked)
    {
        CustomerMask.Name(name).ShouldBe(masked);
    }

    [Fact]
    public void CpfShowsOnlyTheMiddleDigits()
    {
        CustomerMask.Cpf("52998224725").ShouldBe("***.982.247-**");
    }

    [Fact]
    public void BirthDateShowsOnlyTheDay()
    {
        CustomerMask.BirthDate("1990-04-15").ShouldBe("****-**-15");
    }

    [Theory]
    [InlineData("ana.souza@ledger.local", "a***@ledger.local")]
    [InlineData("a@b.co", "a***@b.co")]
    [InlineData("𝓜aria@b.co", "𝓜***@b.co")]
    public void EmailShowsTheFirstCharacterAndTheDomain(string email, string masked)
    {
        CustomerMask.Email(email).ShouldBe(masked);
    }
}
