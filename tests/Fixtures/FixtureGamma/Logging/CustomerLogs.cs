using BuildingBlocks.Logging;
using Microsoft.Extensions.Logging;

namespace FixtureGamma.Logging;

// Mesmo nome do tipo de domínio de Customers: a regra reconhece o tipo pelo nome.
internal sealed record CustomerName(string Value);

// Dado pessoal no log sem a marca: as duas formas que a regra aponta, pelo tipo e pelo nome do parâmetro.
internal static partial class UnmarkedCustomerLogs
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Customer {CustomerId} named {Name}")]
    internal static partial void LogNamed(ILogger logger, Guid customerId, CustomerName name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Customer {CustomerId} reached at {Email}")]
    internal static partial void LogReached(ILogger logger, Guid customerId, string email);
}

// Os mesmos métodos, marcados: a regra os aceita.
internal static partial class MarkedCustomerLogs
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Customer {CustomerId} named {Name}")]
    internal static partial void LogNamed(ILogger logger, Guid customerId, [PersonalData] CustomerName name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Customer {CustomerId} reached at {Email}")]
    internal static partial void LogReached(ILogger logger, Guid customerId, [PersonalData] string email);
}
