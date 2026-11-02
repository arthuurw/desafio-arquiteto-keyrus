extern alias WorkerHost;

using BuildingBlocks.Idempotency;
using IntegrationTests.Infrastructure;
using Ledger.Domain;
using Ledger.Features;
using Ledger.Features.ChangeCreditLimit;
using Ledger.Features.OpenAccount;
using Ledger.Features.RecordEntry;
using Ledger.Features.ReverseEntry;
using Mediator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests.Hosts;

public sealed class WorkerStartupTests(WorkerDependencies dependencies) : IClassFixture<WorkerDependencies>
{
    // Development liga o `ValidateOnBuild`: todo serviço registrado precisa resolver, também os que o Worker não usa.
    [Fact]
    public async Task StartsInDevelopment()
    {
        await using var worker = dependencies.CreateWorker("Development");
        using var client = worker.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(200);
    }

    // O Worker não despacha comando: nem a facade nem um handler do Ledger existem no container dele.
    [Fact]
    public async Task LoadsNoLedgerCommandHandler()
    {
        await using var worker = dependencies.CreateWorker("Development");
        using var scope = worker.Services.CreateScope();
        var services = scope.ServiceProvider;

        Type[] absent =
        [
            typeof(ILedgerCommands),
            typeof(OpenAccountHandler),
            typeof(ICommandHandler<OpenAccountCommand, WriteResult>),
            typeof(RecordEntryHandler),
            typeof(ICommandHandler<RecordEntryCommand, WriteResult>),
            typeof(ReverseEntryHandler),
            typeof(ICommandHandler<ReverseEntryCommand, WriteResult>),
            typeof(ChangeCreditLimitHandler),
            typeof(ICommandHandler<ChangeCreditLimitCommand, Outcome<Account>>),
        ];
        foreach (var type in absent)
        {
            services.GetService(type).ShouldBeNull(type.Name);
        }
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("1.00:00:01")]
    [InlineData("abc")]
    public async Task RejectsInvalidPurgeInterval(string interval)
    {
        using var worker = WorkerProcess.Start(UnreachableDatabase.ConnectionString, interval);

        var exitCode = await worker.WaitForExitAsync(TimeSpan.FromSeconds(60));

        exitCode.ShouldNotBe(0, worker.Output);
        worker.Output.ShouldContain("Idempotency:PurgeInterval");
        worker.Output.ShouldNotContain("Now listening on");
    }

    // As duas pontas da faixa aceita.
    [Theory]
    [InlineData("00:00:01")]
    [InlineData("1.00:00:00")]
    public async Task AcceptsPurgeIntervalAtRangeBounds(string interval)
    {
        using var worker = WorkerProcess.Start(UnreachableDatabase.ConnectionString, interval);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{worker.Port}") };
        await worker.WaitForOutputAsync(output => output.Contains("Now listening on", StringComparison.Ordinal), TimeSpan.FromSeconds(60));

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(200, worker.Output);
        worker.Output.ShouldContain($"Idempotency key purge scheduled every {interval}");
    }

    // Logo além de cada ponta da faixa, e um valor que nem é intervalo.
    [Theory]
    [InlineData("00:00:00.099")]
    [InlineData("00:01:00.001")]
    [InlineData("abc")]
    public async Task RejectsInvalidPollInterval(string interval)
    {
        using var worker = StartWithPollInterval(interval);

        var exitCode = await worker.WaitForExitAsync(TimeSpan.FromSeconds(60));

        exitCode.ShouldNotBe(0, worker.Output);
        worker.Output.ShouldContain("Outbox:PollInterval");
        worker.Output.ShouldContain($"got '{interval}'");
        worker.Output.ShouldNotContain("Now listening on");
    }

    [Theory]
    [InlineData("00:00:00.100", "00:00:00.1000000")]
    [InlineData("00:01:00", "00:01:00")]
    public async Task AcceptsPollIntervalAtRangeBounds(string interval, string logged)
    {
        using var worker = StartWithPollInterval(interval);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{worker.Port}") };
        await worker.WaitForOutputAsync(output => output.Contains("Now listening on", StringComparison.Ordinal), TimeSpan.FromSeconds(60));

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(200, worker.Output);
        worker.Output.ShouldContain($"Outbox relay polling every {logged}");
    }

    private static WorkerProcess StartWithPollInterval(string interval) => WorkerProcess.Start(new Dictionary<string, string>
    {
        ["ConnectionStrings__Ledger"] = UnreachableDatabase.ConnectionString,
        ["Outbox__PollInterval"] = interval,
    });
}

// PostgreSQL migrado e MongoDB reais para o Worker subir como no compose, com as roles dele.
public sealed class WorkerDependencies : IAsyncLifetime
{
    private PostgresDatabase _database = null!;
    private MongoReadModel _mongo = null!;

    public async ValueTask InitializeAsync()
    {
        var postgres = PostgresDatabase.StartAsync();
        var mongo = MongoReadModel.StartAsync();
        _database = await postgres;
        _mongo = await mongo;
        var migration = await MigratorProcess.RunAsync(_database.ConnectionStringFor("migrator"));
        migration.ExitCode.ShouldBe(0, migration.Output);
    }

    internal WebApplicationFactory<WorkerHost::Program> CreateWorker(string environment) =>
        new WebApplicationFactory<WorkerHost::Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment(environment)
            .UseSetting("ConnectionStrings:Ledger", _database.ConnectionStringFor("ledger_worker"))
            .UseSetting("ConnectionStrings:Customers", _database.ConnectionStringFor("customers_worker"))
            .UseSetting("ConnectionStrings:Positions", _mongo.ConnectionStringFor(MongoReadModel.Writer)));

    public async ValueTask DisposeAsync()
    {
        if (_mongo is not null)
        {
            await _mongo.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}
