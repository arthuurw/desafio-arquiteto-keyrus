namespace IntegrationTests.Infrastructure;

// Cliente semeado direto no banco pelo superusuário, sem o cofre: basta para o Ledger abrir conta, que só confere que o
// cliente existe (ADR-0016). Os campos não decifram; nenhum teste do Ledger lê o cliente.
internal static class CustomerRows
{
    internal static Task SeedCustomerAsync(this PostgresDatabase database, Guid customerId) =>
        database.ExecuteAsync(
            "postgres",
            $"""
            INSERT INTO customers.customers (id, name, cpf, birth_date, email, cpf_blind_index, cpf_blind_index_version)
            VALUES ('{customerId:D}', '\x01'::bytea, '\x01'::bytea, '\x01'::bytea, '\x01'::bytea, sha256(gen_random_uuid()::text::bytea), 1)
            ON CONFLICT (id) DO NOTHING
            """);
}
