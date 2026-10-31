namespace IntegrationTests.Ledger;

// O `Idempotency-Key` de um envio de teste. O padrão (`default`) é uma chave nova por chamada,
// como um chamador que gera uma por operação lógica.
internal readonly record struct KeyHeader
{
    private KeyHeader(string? value, bool omitted)
    {
        Value = value;
        Omitted = omitted;
    }

    internal static KeyHeader Fresh => default;

    internal static KeyHeader None { get; } = new(null, omitted: true);

    internal string? Value { get; }

    internal bool Omitted { get; }

    public static implicit operator KeyHeader(string value) => FromString(value);

    internal static KeyHeader FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new KeyHeader(value, omitted: false);
    }

    // O valor que vai no header, ou `null` para enviar sem ele.
    internal string? Resolve() => Omitted ? null : Value ?? Guid.NewGuid().ToString();
}
