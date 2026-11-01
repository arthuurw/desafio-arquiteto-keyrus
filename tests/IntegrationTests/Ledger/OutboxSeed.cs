using IntegrationTests.Infrastructure;

namespace IntegrationTests.Ledger;

// Eventos inseridos direto em `ledger.outbox` como superusuário, com idade e estado escolhidos pelo teste. O payload
// não é um evento projetável: o Worker que os reservar não os aplica.
internal static class OutboxSeed
{
    // `processedAgo` nulo deixa o evento pendente.
    internal static async Task<long> SeedEventAsync(this PostgresDatabase database, string? processedAgo, string createdAgo = "0 seconds")
    {
        var processed = processedAgo is null ? "NULL" : $"now() - interval '{processedAgo}'";
        return await database.ScalarAsync<long>("postgres", $"""
            INSERT INTO ledger.outbox (account_id, sequence, event_type, payload, created_at, processed_at)
            VALUES (gen_random_uuid(), 1, 'Seeded.v1', jsonb_build_object(), now() - interval '{createdAgo}', {processed})
            RETURNING id
            """);
    }

    internal static async Task SeedProcessedEventsAsync(this PostgresDatabase database, int count, string processedAgo) =>
        await database.ExecuteAsync("postgres", $"""
            INSERT INTO ledger.outbox (account_id, sequence, event_type, payload, processed_at)
            SELECT gen_random_uuid(), 1, 'Seeded.v1', jsonb_build_object(), now() - interval '{processedAgo}'
            FROM generate_series(1, {count})
            """);

    internal static async Task<long> OutboxCountAsync(this PostgresDatabase database, string where) =>
        await database.ScalarAsync<long>("postgres", $"SELECT count(*) FROM ledger.outbox WHERE {where}");

    internal static async Task<bool> OutboxEventExistsAsync(this PostgresDatabase database, long id) =>
        await database.OutboxCountAsync($"id = {id}") == 1;
}
