namespace Customers.Domain;

// Nome do cliente, aparado nas pontas; os espaços internos ficam como vieram.
internal sealed record CustomerName
{
    internal const int MaxLength = 150;

    private CustomerName(string value) => Value = value;

    internal string Value { get; }

    internal static CustomerName? Parse(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length is > 0 and <= MaxLength ? new CustomerName(trimmed) : null;
    }
}
