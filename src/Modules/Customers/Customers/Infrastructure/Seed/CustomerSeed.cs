using Customers.Domain;
using Customers.Infrastructure.Persistence;
using Customers.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Customers.Infrastructure.Seed;

// Clientes de desenvolvimento para os usuários `ana` e `bruno` do realm local, com os mesmos `customer_id`. Dados
// fictícios, cifrados como qualquer cadastro. Ligado só no compose (`Customers:Seed:Enabled`); subir de novo não duplica.
internal sealed partial class CustomerSeed(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CustomerSeed> logger) : BackgroundService
{
    internal const string EnabledSetting = "Customers:Seed:Enabled";

    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(2);
    private const int Attempts = 30;

    private static readonly (Guid Id, string Name, string Cpf, string BirthDate, string Email)[] Customers =
    [
        (Guid.Parse("11111111-1111-4111-8111-111111111111"), "Ana Souza", "52998224725", "1990-04-15", "ana.souza@ledger.local"),
        (Guid.Parse("22222222-2222-4222-8222-222222222222"), "Bruno Lima", "11144477735", "1985-09-30", "bruno.lima@ledger.local"),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                await SeedAsync(stoppingToken);
                return;
            }
            catch (PersonalDataUnavailableException) when (attempt < Attempts)
            {
                // O cofre ainda subindo: tenta de novo, sem derrubar a Api.
                await Task.Delay(RetryEvery, clock, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogFailed(exception);
                return;
            }
        }
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var (id, name, cpf, birthDate, email) in Customers)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
            if (await db.Customers.AnyAsync(row => row.Id == id, cancellationToken))
            {
                continue;
            }

            var customer = new Customer(
                id, CustomerName.Parse(name)!, Cpf.Parse(cpf)!, BirthDate.Parse(birthDate, clock.GetUtcNow())!, Email.Parse(email)!);
            var outcome = await scope.ServiceProvider.GetRequiredService<CustomerRegistration>().RegisterAsync(customer, cancellationToken);
            LogSeeded(id, outcome);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Development customer {CustomerId} seeded: {Outcome}")]
    private partial void LogSeeded(Guid customerId, RegistrationOutcome outcome);

    [LoggerMessage(Level = LogLevel.Error, Message = "Development customer seed failed")]
    private partial void LogFailed(Exception exception);
}
