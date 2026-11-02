namespace IntegrationTests.Infrastructure;

// Prazos dos ambientes de teste. O de produção (300 ms no MongoDB, 1 s no cofre) só vale onde o teste provoca o
// estouro; nos demais, com o Docker disputado por toda a suíte, ele falharia sem defeito.
internal static class TestTimeouts
{
    internal const string Budget = "00:00:10";

    internal const string ProductionVault = "00:00:01";
}
