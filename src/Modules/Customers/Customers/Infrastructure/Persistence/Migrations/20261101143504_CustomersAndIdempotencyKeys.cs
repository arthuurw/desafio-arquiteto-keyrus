using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class CustomersAndIdempotencyKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "customers");

            migrationBuilder.CreateTable(
                name: "customers",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<byte[]>(type: "bytea", nullable: false),
                    cpf = table.Column<byte[]>(type: "bytea", nullable: false),
                    birth_date = table.Column<byte[]>(type: "bytea", nullable: false),
                    email = table.Column<byte[]>(type: "bytea", nullable: false),
                    cpf_blind_index = table.Column<byte[]>(type: "bytea", nullable: false),
                    cpf_blind_index_version = table.Column<int>(type: "integer", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "customers",
                columns: table => new
                {
                    client_id = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.client_id, x.idempotency_key });
                });

            migrationBuilder.CreateIndex(
                name: "ux_customers_cpf_blind_index",
                schema: "customers",
                table: "customers",
                column: "cpf_blind_index",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_created_at",
                schema: "customers",
                table: "idempotency_keys",
                column: "created_at");

            // Sem UPDATE por padrão no schema: nenhuma coluna de `customers` muda depois do cadastro, e das chaves só a
            // resposta. Sem DELETE: a limpeza passa pela função abaixo, executada só pelo `customers_worker` (ADR-0038).
            migrationBuilder.Sql(
                """
                ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA customers REVOKE UPDATE ON TABLES FROM customers_app;
                REVOKE UPDATE ON customers.customers, customers.idempotency_keys FROM customers_app;
                GRANT UPDATE (response_status, response_body) ON customers.idempotency_keys TO customers_app;
                """);

            // Mesmo desenho da limpeza do Ledger (ADR-0037): prazo fixo de 7 dias e lote limitado por chamada.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION customers.purge_expired_idempotency_keys(batch_size integer) RETURNS bigint
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = customers, pg_temp
                AS $$
                DECLARE
                    purged bigint;
                BEGIN
                    IF batch_size IS NULL OR batch_size < 1 OR batch_size > 10000 THEN
                        RAISE EXCEPTION 'batch_size must be between 1 and 10000, got %', batch_size
                            USING ERRCODE = 'invalid_parameter_value';
                    END IF;

                    WITH expired AS (
                        SELECT client_id, idempotency_key
                        FROM customers.idempotency_keys
                        WHERE created_at < now() - interval '7 days'
                        ORDER BY created_at
                        LIMIT batch_size
                    )
                    DELETE FROM customers.idempotency_keys AS k
                    USING expired AS e
                    WHERE k.client_id = e.client_id AND k.idempotency_key = e.idempotency_key;
                    GET DIAGNOSTICS purged = ROW_COUNT;
                    RETURN purged;
                END
                $$;
                REVOKE ALL ON FUNCTION customers.purge_expired_idempotency_keys(integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION customers.purge_expired_idempotency_keys(integer) TO customers_worker;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION customers.purge_expired_idempotency_keys(integer);
                ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA customers GRANT UPDATE ON TABLES TO customers_app;
                """);

            migrationBuilder.DropTable(
                name: "customers",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "customers");
        }
    }
}
