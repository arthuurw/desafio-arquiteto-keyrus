using System.ComponentModel;
using BuildingBlocks.Http;
using BuildingBlocks.Identity;
using BuildingBlocks.Idempotency;
using Customers.Domain;
using Customers.Infrastructure.Idempotency;
using Customers.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Customers.Features.RegisterCustomer;

// Valores já normalizados: o CPF com ou sem pontuação é a mesma requisição. O id não entra: nasce no handler.
internal sealed record RegisterCustomerCommand(CustomerName Name, Cpf Cpf, BirthDate BirthDate, Email Email, Caller Caller, Guid IdempotencyKey)
    : IIdempotentCommand
{
    public IReadOnlyList<string> RequestFields =>
        [IdempotentOperation.RegisterCustomer, Name.Value, Cpf.Digits, BirthDate.Iso, Email.Value];
}

// A resposta guarda só o id: a linha da chave não leva dado pessoal (ADR-0037). O CPF repetido é recusa de negócio,
// guardada com a chave como as demais.
internal sealed class RegisterCustomerHandler(CustomerRegistration registration) : ICommandHandler<RegisterCustomerCommand, WriteResult>
{
    public async ValueTask<WriteResult> Handle(RegisterCustomerCommand command, CancellationToken cancellationToken)
    {
        var customer = Customer.Register(command.Name, command.Cpf, command.BirthDate, command.Email);
        return await registration.RegisterAsync(customer, cancellationToken) == RegistrationOutcome.Registered
            ? WriteResult.Created(new CustomerCreated(customer.Id))
            : WriteResult.Problem(CustomersProblems.CpfAlreadyRegistered());
    }
}

// Corpo do cadastro, para o contrato publicado; a leitura é do CustomersInput, que junta todos os erros.
internal sealed record RegisterCustomerRequest(
    [property: Description("Nome completo, de 1 a 150 caracteres; espaços nas pontas são removidos.")] string Name,
    [property: Description("CPF com 11 dígitos ou no formato 000.000.000-00.")] string Cpf,
    [property: Description("Data de nascimento no formato yyyy-MM-dd; o cliente precisa ter 18 anos ou mais.")] string BirthDate,
    [property: Description("E-mail de contato, até 254 caracteres.")] string Email);

internal sealed record CustomerCreated([property: Description("Id do cliente cadastrado.")] Guid Id);

internal static class RegisterCustomerEndpoint
{
    internal static void Map(IEndpointRouteBuilder customers) =>
        customers.MapPost("/", async (HttpRequest request, Caller caller, ICustomersCommands commands, TimeProvider clock, CancellationToken cancellationToken) =>
            {
                if (!IdempotencyKeyHeader.TryRead(request, out var idempotencyKey, out var keyProblem))
                {
                    return keyProblem;
                }

                var input = await CustomersInput.ReadAsync(request, cancellationToken);
                var name = input.Name("name");
                var cpf = input.Cpf("cpf");
                var birthDate = input.BirthDate("birthDate", clock.GetUtcNow());
                var email = input.Email("email");
                if (!input.IsValid || name is null || cpf is null || birthDate is null || email is null)
                {
                    return ProblemResponses.Validation(input.Errors);
                }

                var result = await commands.Send(new RegisterCustomerCommand(name, cpf, birthDate, email, caller, idempotencyKey), cancellationToken);
                return result.ToHttpResult();
            })
            .RequireScope(Scopes.CustomersWrite)
            .Accepts<RegisterCustomerRequest>("application/json")
            .Produces<CustomerCreated>(StatusCodes.Status201Created)
            .Documented(
                "Cadastra um cliente",
                "Cadastra o cliente e devolve só o id. Nome, CPF, nascimento e e-mail são gravados cifrados, com uma chave "
                    + "própria do cliente no cofre; o CPF é único entre os clientes.",
                idempotencyKey: true,
                new Dictionary<int, string[]>
                {
                    [StatusCodes.Status400BadRequest] = [ProblemResponses.ValidationErrorType],
                    [StatusCodes.Status409Conflict] = [CustomersProblems.CpfAlreadyRegistered().Type!],
                });
}
