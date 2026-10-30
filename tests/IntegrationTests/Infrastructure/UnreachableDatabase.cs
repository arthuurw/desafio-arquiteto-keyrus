namespace IntegrationTests.Infrastructure;

// Porta 1: nenhum PostgreSQL escuta ali. Prova que o caminho testado não depende do banco (ADR-0021).
internal static class UnreachableDatabase
{
    internal const string ConnectionString = "Host=127.0.0.1;Port=1;Database=ledger_db;Username=ledger_app;Password=ledger_app_dev";
}
