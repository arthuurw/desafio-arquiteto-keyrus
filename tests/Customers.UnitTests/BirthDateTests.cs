using System.Globalization;
using Customers.Domain;

namespace Customers.UnitTests;

public sealed class BirthDateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T12:00:00Z", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2008-11-01")] // 18 anos hoje
    [InlineData("1900-01-01")]
    [InlineData("1990-04-15")]
    public void AcceptsAdultsBornFrom1900(string value)
    {
        BirthDate.Parse(value, Now).ShouldNotBeNull().Iso.ShouldBe(value);
    }

    [Theory]
    [InlineData("2008-11-02")] // 18 anos amanhã
    [InlineData("1899-12-31")]
    [InlineData("2000-02-30")]
    [InlineData("01/04/1990")]
    [InlineData("2030-01-01")]
    [InlineData("1990-4-15")]
    [InlineData("ontem")]
    public void RefusesMinorsImpossibleDatesAndOtherFormats(string value)
    {
        BirthDate.Parse(value, Now).ShouldBeNull();
    }

    [Fact]
    public void AgeIsCountedOnTheUtcDay()
    {
        // 23h de 31/10 em São Paulo já é 01/11 em UTC: quem nasceu em 01/11/2008 já tem 18 anos.
        var lateEvening = DateTimeOffset.Parse("2026-10-31T23:00:00-03:00", CultureInfo.InvariantCulture);

        BirthDate.Parse("2008-11-01", lateEvening).ShouldNotBeNull();
        BirthDate.Parse("2008-11-02", lateEvening).ShouldBeNull();
    }
}
