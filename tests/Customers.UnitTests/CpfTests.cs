using Customers.Domain;

namespace Customers.UnitTests;

public sealed class CpfTests
{
    [Theory]
    [InlineData("529.982.247-25")]
    [InlineData("52998224725")]
    public void AcceptsTheSameCpfWithOrWithoutPunctuation(string value)
    {
        var cpf = Cpf.Parse(value).ShouldNotBeNull();

        cpf.Digits.ShouldBe("52998224725");
        cpf.Formatted.ShouldBe("529.982.247-25");
    }

    [Theory]
    [InlineData("529.982.247-24")] // segundo verificador errado
    [InlineData("529.982.247-15")] // primeiro verificador errado
    [InlineData("111.111.111-11")] // dígitos iguais
    [InlineData("00000000000")]
    [InlineData("5299822472")] // 10 dígitos
    [InlineData("529982247250")] // 12 dígitos
    [InlineData("529.982.24725")]
    [InlineData("529-982-247.25")]
    [InlineData("5299822472a")]
    [InlineData("")]
    public void RefusesAnythingThatIsNotAValidCpf(string value)
    {
        Cpf.Parse(value).ShouldBeNull();
    }

    [Theory]
    [InlineData("11144477735", "111.444.777-35")]
    [InlineData("39053344705", "390.533.447-05")]
    [InlineData("12345678909", "123.456.789-09")]
    public void AcceptsOtherValidCpfs(string digits, string formatted)
    {
        Cpf.Parse(digits).ShouldNotBeNull().Formatted.ShouldBe(formatted);
        Cpf.Parse(formatted).ShouldNotBeNull().Digits.ShouldBe(digits);
    }
}
