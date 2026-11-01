using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class IdempotencyKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "ledger",
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
                name: "ux_entries_client_id_external_reference",
                schema: "ledger",
                table: "entries",
                columns: new[] { "client_id", "external_reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_created_at",
                schema: "ledger",
                table: "idempotency_keys",
                column: "created_at");

            // A aplicação não tem DELETE (ADR-0043): a limpeza passa por esta função do migrator, que só apaga
            // chaves vencidas. O prazo fica fixo aqui para quem tem EXECUTE não apagar chave recente.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION ledger.purge_expired_idempotency_keys() RETURNS bigint
                LANGUAGE sql
                SECURITY DEFINER
                SET search_path = ledger, pg_temp
                AS $$
                    WITH purged AS (
                        DELETE FROM ledger.idempotency_keys WHERE created_at < now() - interval '7 days' RETURNING 1
                    )
                    SELECT count(*) FROM purged;
                $$;
                REVOKE ALL ON FUNCTION ledger.purge_expired_idempotency_keys() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION ledger.purge_expired_idempotency_keys() TO ledger_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION ledger.purge_expired_idempotency_keys();");

            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "ledger");

            migrationBuilder.DropIndex(
                name: "ux_entries_client_id_external_reference",
                schema: "ledger",
                table: "entries");
        }
    }
}
