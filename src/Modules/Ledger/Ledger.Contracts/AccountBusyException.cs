namespace Ledger.Contracts;

// A conta segue travada por uma escrita depois do `lock_timeout` da leitura que espera por ela (ADR-0045).
public sealed class AccountBusyException : Exception
{
    private const string Locked = "The account is locked by a write in progress.";

    public AccountBusyException()
        : base(Locked)
    {
    }

    public AccountBusyException(Exception innerException)
        : base(Locked, innerException)
    {
    }

    public AccountBusyException(string message)
        : base(message)
    {
    }

    public AccountBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
