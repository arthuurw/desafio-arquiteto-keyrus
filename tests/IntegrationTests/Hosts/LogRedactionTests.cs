using BuildingBlocks.Logging;
using IntegrationTests.Ledger;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntegrationTests.Hosts;

// A redação vem da configuração de logging da Api, não do código que loga: o valor marcado sai apagado (ADR-0041).
[Collection(LedgerApiDefinition.Name)]
public sealed partial class LogRedactionTests(LedgerApi api)
{
    private const string Category = "RedactionProbe";

    [Fact]
    public void ClassifiedParameterIsErased()
    {
        var logger = api.Services.GetRequiredService<ILoggerFactory>().CreateLogger(Category);
        var customerId = Guid.NewGuid();
        var mark = api.ApiLog.Mark();

        LogNamed(logger, customerId, "Elisa Fontes Barreto");

        var line = api.ApiLog.Since(mark).Where(entry => entry.Category == Category).ShouldHaveSingleItem();
        // Só o nome some: o id fica, e o texto em volta do nome também.
        line.Message.ShouldBe($"Customer {customerId} named ");
        foreach (var part in new[] { "Elisa", "Fontes", "Barreto" })
        {
            line.Message.ShouldNotContain(part);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Customer {CustomerId} named {Name}")]
    private static partial void LogNamed(ILogger logger, Guid customerId, [PersonalData] string name);
}
