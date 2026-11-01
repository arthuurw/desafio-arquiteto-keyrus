namespace IntegrationTests.Infrastructure;

// Porta 1: nenhum banco escuta ali. Prova que o caminho testado não depende do banco (ADR-0021).
internal static class UnreachableDatabase
{
    internal const string ConnectionString = "Host=127.0.0.1;Port=1;Database=ledger_db;Username=ledger_app;Password=ledger_app_dev";

    internal const string MongoConnectionString = "mongodb://positions_reader:positions_reader_dev@127.0.0.1:1/positions?authSource=positions";
}
