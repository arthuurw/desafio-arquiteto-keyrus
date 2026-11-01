using Customers.Domain;

namespace Customers.UnitTests;

public sealed class CustomerNameTests
{
    [Fact]
    public void TrimsTheEnds()
    {
        CustomerName.Parse("  Ana Souza  ").ShouldNotBeNull().Value.ShouldBe("Ana Souza");
    }

    [Fact]
    public void AcceptsUpTo150CharactersAfterTrimming()
    {
        var name = new string('a', 150);

        CustomerName.Parse($"  {name}  ").ShouldNotBeNull().Value.ShouldBe(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void RefusesEmptyNames(string value)
    {
        CustomerName.Parse(value).ShouldBeNull();
    }

    [Fact]
    public void Refuses151CharactersAfterTrimming()
    {
        CustomerName.Parse($" {new string('a', 151)} ").ShouldBeNull();
    }
}
