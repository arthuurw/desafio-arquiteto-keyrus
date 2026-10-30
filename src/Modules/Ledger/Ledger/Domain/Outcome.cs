using System.Diagnostics.CodeAnalysis;

namespace Ledger.Domain;

// Resultado de uma operação: o valor produzido ou a recusa, nunca os dois.
internal readonly record struct Outcome<T>
    where T : class
{
    private Outcome(T? value, LedgerError? error)
    {
        Value = value;
        Error = error;
    }

    internal T? Value { get; }

    internal LedgerError? Error { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    internal bool IsSuccess => Value is not null;

    internal static Outcome<T> Success(T value) => new(value, null);

    internal static Outcome<T> Failure(LedgerError error) => new(null, error);

    public static implicit operator Outcome<T>(LedgerError error) => Failure(error);
}
