using Customers.Domain;

namespace Customers.UnitTests;

public sealed class EmailTests
{
    [Fact]
    public void AcceptsAShortAddressAndOneOf254Characters()
    {
        Email.Parse("a@b.co").ShouldNotBeNull().Value.ShouldBe("a@b.co");
        var longest = AddressOfLength(254);
        Email.Parse(longest).ShouldNotBeNull().Value.ShouldBe(longest);
    }

    [Theory]
    [InlineData("a@b")]
    [InlineData("a@@b.co")]
    [InlineData("@b.co")]
    [InlineData("a@")]
    [InlineData("a b@b.co")]
    [InlineData("a@b..co")]
    [InlineData("a@b.co\nx")]
    [InlineData("")]
    public void RefusesMalformedAddresses(string value)
    {
        Email.Parse(value).ShouldBeNull();
    }

    [Fact]
    public void RefusesAnAddressOf255Characters()
    {
        Email.Parse(AddressOfLength(255)).ShouldBeNull();
    }

    [Fact]
    public void TrimsTheEnds()
    {
        Email.Parse("  a@b.co ").ShouldNotBeNull().Value.ShouldBe("a@b.co");
    }

    // `a@` e um domínio de rótulos com até 63 letras e um `.co` final, com o tamanho total pedido.
    private static string AddressOfLength(int length)
    {
        var labels = new List<string>();
        var remaining = length - "a@".Length - ".co".Length;
        while (remaining > 0)
        {
            var label = Math.Min(63, remaining - (labels.Count == 0 ? 0 : 1));
            labels.Add(new string('d', label));
            remaining -= label + (labels.Count == 1 ? 0 : 1);
        }

        var address = "a@" + string.Join('.', labels) + ".co";
        address.Length.ShouldBe(length);
        return address;
    }
}
