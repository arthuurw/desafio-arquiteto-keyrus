using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class LeastPrivilegeAndBatchedPurge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "created_at",
                schema: "ledger",
                table: "accounts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "clock_timestamp()",
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            // Um DELETE só ficaria preso ao timeout de comando depois de um acúmulo grande: cada chamada apaga um
            // lote, na própria transação, e o Worker repete. O teto do lote impede uma chamada que volte ao problema.
            migrationBuilder.Sql(
                """
                DROP FUNCTION ledger.purge_expired_idempotency_keys();
                CREATE FUNCTION ledger.purge_expired_idempotency_keys(batch_size integer) RETURNS bigint
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = ledger, pg_temp
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
                        FROM ledger.idempotency_keys
                        WHERE created_at < now() - interval '7 days'
                        ORDER BY created_at
                        LIMIT batch_size
                    )
                    DELETE FROM ledger.idempotency_keys AS k
                    USING expired AS e
                    WHERE k.client_id = e.client_id AND k.idempotency_key = e.idempotency_key;
                    GET DIAGNOSTICS purged = ROW_COUNT;
                    RETURN purged;
                END
                $$;
                REVOKE ALL ON FUNCTION ledger.purge_expired_idempotency_keys(integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION ledger.purge_expired_idempotency_keys(integer) TO ledger_worker;
                """);

            // UPDATE só nas colunas que a aplicação escreve: titular, criação e prazo da chave ficam fora do alcance
            // de quem tem a credencial da Api. Tabela nova concede UPDATE por coluna na própria migração.
            // O histórico de migração é só do migrator: uma linha inserida ali faria uma migração ser pulada.
            migrationBuilder.Sql(
                """
                ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA ledger REVOKE UPDATE ON TABLES FROM ledger_app;
                REVOKE UPDATE ON ledger.accounts, ledger.idempotency_keys FROM ledger_app;
                GRANT UPDATE (balance, credit_limit, version, last_recorded_at) ON ledger.accounts TO ledger_app;
                GRANT UPDATE (response_status, response_body) ON ledger.idempotency_keys TO ledger_app;
                REVOKE ALL ON ledger."__EFMigrationsHistory" FROM ledger_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                GRANT SELECT, INSERT, UPDATE ON ledger."__EFMigrationsHistory" TO ledger_app;
                REVOKE UPDATE (response_status, response_body) ON ledger.idempotency_keys FROM ledger_app;
                REVOKE UPDATE (balance, credit_limit, version, last_recorded_at) ON ledger.accounts FROM ledger_app;
                GRANT UPDATE ON ledger.accounts, ledger.idempotency_keys TO ledger_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA ledger GRANT UPDATE ON TABLES TO ledger_app;
                """);

            migrationBuilder.Sql(
                """
                DROP FUNCTION ledger.purge_expired_idempotency_keys(integer);
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

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "created_at",
                schema: "ledger",
                table: "accounts",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "clock_timestamp()");
        }
    }
}
