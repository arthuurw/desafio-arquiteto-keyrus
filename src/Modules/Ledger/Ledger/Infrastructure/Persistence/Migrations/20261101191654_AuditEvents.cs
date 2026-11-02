using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Trilha de auditoria do módulo (ADR-0041): só identificadores, sem chave estrangeira, porque a tentativa
            // recusada aponta para recurso que não existe.
            migrationBuilder.Sql(
                """
                CREATE TABLE ledger.audit_events (
                    id uuid NOT NULL,
                    occurred_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                    client_id text NULL,
                    caller_customer_id uuid NULL,
                    operation text NOT NULL,
                    resource_type text NOT NULL,
                    resource_id uuid NULL,
                    result integer NOT NULL,
                    trace_id text NOT NULL,
                    CONSTRAINT pk_audit_events PRIMARY KEY (id)
                );
                CREATE INDEX ix_audit_events_occurred_at ON ledger.audit_events (occurred_at);
                """);

            // A Api só insere; o trigger recusa o DML de qualquer role, inclusive a dona e o superusuário (ADR-0044).
            migrationBuilder.Sql(
                """
                REVOKE ALL ON ledger.audit_events FROM ledger_app;
                GRANT INSERT ON ledger.audit_events TO ledger_app;

                CREATE FUNCTION ledger.reject_audit_change() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = ledger, pg_temp
                AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_events is append-only: % refused', TG_OP;
                END
                $$;
                REVOKE ALL ON FUNCTION ledger.reject_audit_change() FROM PUBLIC;

                CREATE TRIGGER audit_events_reject_row_change
                    BEFORE UPDATE OR DELETE ON ledger.audit_events
                    FOR EACH ROW EXECUTE FUNCTION ledger.reject_audit_change();
                CREATE TRIGGER audit_events_reject_truncate
                    BEFORE TRUNCATE ON ledger.audit_events
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_audit_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE ledger.audit_events;
                DROP FUNCTION ledger.reject_audit_change();
                """);
        }
    }
}
