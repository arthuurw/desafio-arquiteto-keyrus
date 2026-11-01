using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using BuildingBlocks.Http;
using BuildingBlocks.Identity;
using Customers.Domain;
using Customers.Infrastructure.Crypto;
using Customers.Infrastructure.Persistence;
using Customers.Infrastructure.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Customers.Features.ReadCustomer;

// O cliente como as duas leituras o devolvem: mascarado ou completo, com o mesmo formato.
internal sealed record CustomerResponse(
    [property: Description("Id do cliente.")] Guid Id,
    [property: Description("Nome.")] string Name,
    [property: Description("CPF.")] string Cpf,
    [property: Description("Data de nascimento.")] string BirthDate,
    [property: Description("E-mail de contato.")] string Email,
    [property: Description("Instante do cadastro, em UTC.")] DateTime RegisteredAt)
{
    internal static CustomerResponse Masked(CustomerData data) =>
        new(data.Id, CustomerMask.Name(data.Name), CustomerMask.Cpf(data.CpfDigits), CustomerMask.BirthDate(data.BirthDate),
            CustomerMask.Email(data.Email), data.RegisteredAt.UtcDateTime);

    internal static CustomerResponse Complete(CustomerData data) =>
        new(data.Id, data.Name, Domain.Cpf.Parse(data.CpfDigits)!.Formatted, data.BirthDate, data.Email, data.RegisteredAt.UtcDateTime);
}

// Desfecho de uma leitura: os dados, ou o status e a resposta da recusa.
internal sealed record CustomerRead(CustomerData? Data, int Status, IResult? Refusal)
{
    internal static CustomerRead Found(CustomerData data) => new(data, StatusCodes.Status200OK, null);

    internal static CustomerRead NotFound() =>
        new(null, StatusCodes.Status404NotFound, ProblemResponses.Result(CustomersProblems.CustomerNotFound()));
}

// Leitura de um cliente: titularidade, linha, DEK e decifragem. O delegado só lê o próprio cliente, e qualquer outro
// responde como inexistente, sem tocar o banco (ADR-0013).
internal sealed partial class CustomerReader(CustomersDbContext db, CustomerKeys keys, ILogger<CustomerReader> logger)
{
    internal async Task<CustomerRead> ReadAsync(Guid? customerId, Caller caller, CancellationToken cancellationToken)
    {
        if (customerId is not { } id || !caller.CanAccessCustomer(id))
        {
            return CustomerRead.NotFound();
        }

        var row = await db.Customers.AsNoTracking().SingleOrDefaultAsync(customer => customer.Id == id, cancellationToken);
        if (row is null)
        {
            return CustomerRead.NotFound();
        }

        byte[] dek;
        try
        {
            dek = await keys.ReadAsync(id, cancellationToken);
        }
        catch (PersonalDataUnavailableException)
        {
            return new CustomerRead(null, StatusCodes.Status503ServiceUnavailable, CustomersProblems.UnavailableResult());
        }

        try
        {
            return CustomerRead.Found(CustomerSeal.Open(row, dek));
        }
        catch (CustomerDataIntegrityException exception)
        {
            // Só o id e o nome do campo: os bytes do campo e o texto parcial nunca vão para o log.
            LogIntegrityFailure(exception.CustomerId, exception.Field);
            return new CustomerRead(
                null, StatusCodes.Status500InternalServerError, ProblemResponses.Result(ProblemResponses.InternalError()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Customer {CustomerId} field {Field} failed integrity verification")]
    private partial void LogIntegrityFailure(Guid customerId, string field);
}

// Registro de cada acesso aos dados completos, só com identificadores (ADR-0042): quem, em nome de quem, qual cliente,
// o resultado e o `traceId` que liga ao resto da requisição.
internal static partial class PersonalDataAudit
{
    internal const string Category = "Audit";
    internal const string Operation = "read-personal-data";

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "{Operation} by client {Client} for customer_id {CallerCustomerId}: customer {CustomerId}, result {Result}, traceId {TraceId}")]
    internal static partial void Log(
        ILogger logger, string operation, string client, string callerCustomerId, string customerId, int result, string traceId);

    // Sem chamador resolvido, como na recusa de autenticação, quem e em nome de quem saem como `-`.
    internal static void Record(HttpContext context, Caller? caller, Guid? customerId, int result)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(Category);
        var client = caller?.ClientId ?? "-";
        var callerCustomer = caller?.CustomerId?.ToString("D") ?? "-";
        var consulted = customerId?.ToString("D") ?? "invalid-id";
        var traceId = ProblemResponses.TraceId(context);
        Log(logger, Operation, client, callerCustomer, consulted, result, traceId);
    }
}

internal static class ReadCustomerEndpoints
{
    internal static void Map(IEndpointRouteBuilder customers)
    {
        customers.MapGet("/{id}", async (string id, Caller caller, CustomerReader reader, CancellationToken cancellationToken) =>
            {
                var read = await reader.ReadAsync(Parse(id), caller, cancellationToken);
                return read.Refusal ?? Results.Ok(CustomerResponse.Masked(read.Data!));
            })
            .RequireScope(Scopes.CustomersRead)
            .Produces<CustomerResponse>(StatusCodes.Status200OK)
            .Documented(
                "Consulta um cliente, mascarado",
                "Devolve o cliente com nome, CPF, nascimento e e-mail mascarados: primeiro nome e iniciais, `***.982.247-**`, "
                    + "`****-**-15`, `a***@dominio`. No modo delegado, só o cliente em nome de quem o token age.",
                idempotencyKey: false,
                new Dictionary<int, string[]> { [StatusCodes.Status404NotFound] = [CustomersProblems.CustomerNotFound().Type!] });

        customers.MapGet("/{id}/personal-data", async (
                string id, Caller caller, CustomerReader reader, HttpContext context, CancellationToken cancellationToken) =>
            {
                var customerId = Parse(id);
                // O acesso é registrado em todo desfecho do handler (ADR-0042). A exceção, como DEK ausente ou recusa do
                // cofre, sobe para o tratador global, que responde `500`; o cliente que desistiu não recebe resposta, e o
                // registro leva o `499` que o ASP.NET Core dá à requisição abortada.
                var status = StatusCodes.Status500InternalServerError;
                try
                {
                    var read = await reader.ReadAsync(customerId, caller, cancellationToken);
                    var result = read.Refusal ?? Results.Ok(CustomerResponse.Complete(read.Data!));
                    status = read.Status;
                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    status = StatusCodes.Status499ClientClosedRequest;
                    throw;
                }
                finally
                {
                    PersonalDataAudit.Record(context, caller, customerId, status);
                }
            })
            .RequireScope(Scopes.CustomersPiiRead)
            // 401 e 403 saem antes do handler; o registro deles vem da autorização.
            .ObserveAuthorizationRefusals((context, status) =>
                PersonalDataAudit.Record(
                    context, Caller.FromPrincipal(context.User), Parse(context.Request.RouteValues["id"] as string ?? ""), status))
            .Produces<CustomerResponse>(StatusCodes.Status200OK)
            .Documented(
                "Consulta os dados pessoais completos de um cliente",
                "Devolve nome, CPF formatado, nascimento e e-mail como cadastrados. Todo acesso, com sucesso ou não, é registrado "
                    + "no log de auditoria só com identificadores. No modo delegado, só o cliente em nome de quem o token age.",
                idempotencyKey: false,
                new Dictionary<int, string[]> { [StatusCodes.Status404NotFound] = [CustomersProblems.CustomerNotFound().Type!] });
    }

    // Id fora do formato não existe: responde como um id inexistente.
    private static Guid? Parse(string id) => Guid.TryParse(id, CultureInfo.InvariantCulture, out var customerId) ? customerId : null;
}
