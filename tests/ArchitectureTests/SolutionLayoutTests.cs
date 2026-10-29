using System.Reflection;

namespace ArchitectureTests;

public sealed class SolutionLayoutTests
{
    [Theory]
    [InlineData("Api")]
    [InlineData("Worker")]
    [InlineData("Migrator")]
    [InlineData("BuildingBlocks")]
    [InlineData("Ledger")]
    [InlineData("Ledger.Contracts")]
    [InlineData("Positions")]
    [InlineData("Positions.Contracts")]
    [InlineData("Customers")]
    [InlineData("Customers.Contracts")]
    public void ExpectedAssembliesAreLoaded(string assemblyName)
    {
        var assembly = Assembly.Load(new AssemblyName(assemblyName));

        assembly.GetName().Name.ShouldBe(assemblyName);
    }
}
