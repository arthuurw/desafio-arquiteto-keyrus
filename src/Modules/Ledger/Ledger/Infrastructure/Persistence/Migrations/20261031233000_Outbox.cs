using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class Outbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Um evento por lançamento, gravado na transação dele (ADR-0006). `id` é a ordem de reserva do relay;
            // a Api só insere: ler, marcar e apagar passam pelas funções do Worker (ADR-0035).
            migrationBuilder.Sql(
                """
                CREATE TABLE ledger.outbox (
                    id bigint GENERATED ALWAYS AS IDENTITY,
                    account_id uuid NOT NULL,
                    sequence bigint NOT NULL,
                    event_type text NOT NULL,
                    payload jsonb NOT NULL,
                    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                    processed_at timestamp with time zone NULL,
                    CONSTRAINT pk_outbox PRIMARY KEY (id),
                    CONSTRAINT ux_outbox_account_id_sequence UNIQUE (account_id, sequence)
                );
                CREATE INDEX ix_outbox_pending ON ledger.outbox (id) WHERE processed_at IS NULL;
                CREATE INDEX ix_outbox_processed_at ON ledger.outbox (processed_at) WHERE processed_at IS NOT NULL;
                REVOKE ALL ON ledger.outbox FROM ledger_app;
                GRANT INSERT ON ledger.outbox TO ledger_app;
                """);

            // Lançamentos de antes do outbox viram eventos pendentes, em ordem de `sequence` por conta: sem eles a
            // projeção esperaria para sempre pela primeira versão de uma conta com histórico.
            migrationBuilder.Sql(
                """
                INSERT INTO ledger.outbox (account_id, sequence, event_type, payload)
                SELECT e.account_id, e.sequence, 'EntryRecorded.v1', jsonb_build_object(
                    'entryId', e.id,
                    'accountId', e.account_id,
                    'sequence', e.sequence,
                    'type', e.type,
                    'amount', e.amount::numeric(19,2),
                    'balanceAfter', e.balance_after::numeric(19,2),
                    'category', e.category,
                    'externalReference', e.external_reference,
                    'reversedEntryId', e.reversed_entry_id,
                    'recordedAt', to_char(e.recorded_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'))
                FROM ledger.entries AS e
                ORDER BY e.account_id, e.sequence;
                """);

            // Funções do Worker, no desenho do ADR-0033: uma operação cada, `search_path` fixo, EXECUTE só ao Worker.
            // A reserva trava os eventos na transação de quem chamou; outra réplica os pula. `after_id` deixa o relay
            // passar de um lote cheio que não anda, para eventos que nunca aplicam não segurarem os demais (ADR-0035).
            migrationBuilder.Sql(
                """
                CREATE FUNCTION ledger.claim_outbox_batch(batch_size integer, after_id bigint)
                RETURNS TABLE (id bigint, event_type text, payload jsonb)
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = ledger, pg_temp
                AS $$
                BEGIN
                    IF batch_size IS NULL OR batch_size < 1 OR batch_size > 1000 THEN
                        RAISE EXCEPTION 'batch_size must be between 1 and 1000, got %', batch_size
                            USING ERRCODE = 'invalid_parameter_value';
                    END IF;

                    IF after_id IS NULL OR after_id < 0 THEN
                        RAISE EXCEPTION 'after_id must be zero or positive, got %', after_id
                            USING ERRCODE = 'invalid_parameter_value';
                    END IF;

                    RETURN QUERY
                    SELECT o.id, o.event_type, o.payload
                    FROM ledger.outbox AS o
                    WHERE o.processed_at IS NULL AND o.id > after_id
                    ORDER BY o.id
                    LIMIT batch_size
                    FOR UPDATE SKIP LOCKED;
                END
                $$;

                CREATE FUNCTION ledger.mark_outbox_processed(ids bigint[]) RETURNS bigint
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = ledger, pg_temp
                AS $$
                DECLARE
                    marked bigint;
                BEGIN
                    UPDATE ledger.outbox
                    SET processed_at = clock_timestamp()
                    WHERE id = ANY (ids) AND processed_at IS NULL;
                    GET DIAGNOSTICS marked = ROW_COUNT;
                    RETURN marked;
                END
                $$;

                CREATE FUNCTION ledger.purge_processed_outbox(batch_size integer) RETURNS bigint
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
                        SELECT o.id
                        FROM ledger.outbox AS o
                        WHERE o.processed_at < now() - interval '7 days'
                        ORDER BY o.processed_at
                        LIMIT batch_size
                    )
                    DELETE FROM ledger.outbox AS o
                    USING expired AS e
                    WHERE o.id = e.id;
                    GET DIAGNOSTICS purged = ROW_COUNT;
                    RETURN purged;
                END
                $$;

                REVOKE ALL ON FUNCTION ledger.claim_outbox_batch(integer, bigint) FROM PUBLIC;
                REVOKE ALL ON FUNCTION ledger.mark_outbox_processed(bigint[]) FROM PUBLIC;
                REVOKE ALL ON FUNCTION ledger.purge_processed_outbox(integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION ledger.claim_outbox_batch(integer, bigint) TO ledger_worker;
                GRANT EXECUTE ON FUNCTION ledger.mark_outbox_processed(bigint[]) TO ledger_worker;
                GRANT EXECUTE ON FUNCTION ledger.purge_processed_outbox(integer) TO ledger_worker;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION ledger.purge_processed_outbox(integer);
                DROP FUNCTION ledger.mark_outbox_processed(bigint[]);
                DROP FUNCTION ledger.claim_outbox_batch(integer, bigint);
                DROP TABLE ledger.outbox;
                """);
        }
    }
}
