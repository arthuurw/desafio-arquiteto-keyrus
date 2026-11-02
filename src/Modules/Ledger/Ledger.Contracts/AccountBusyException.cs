namespace Ledger.Contracts;

// A conta segue travada por uma escrita depois do `lock_timeout` da leitura que espera por ela (ADR-0045).
public sealed class AccountBusyException : Exception
{
    public AccountBusyException()
        : base("The account is locked by a write in progress.")
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
