using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AccountsAndEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ledger");

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    balance = table.Column<decimal>(type: "numeric(19,4)", nullable: false, defaultValueSql: "0"),
                    credit_limit = table.Column<decimal>(type: "numeric(19,4)", nullable: false, defaultValueSql: "0"),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    last_recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.CheckConstraint("ck_accounts_balance_within_limit", "balance >= -credit_limit");
                    table.CheckConstraint("ck_accounts_credit_limit_not_negative", "credit_limit >= 0");
                });

            migrationBuilder.CreateTable(
                name: "entries",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    external_reference = table.Column<string>(type: "text", nullable: false),
                    client_id = table.Column<string>(type: "text", nullable: false),
                    reversed_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entries", x => x.id);
                    table.CheckConstraint("ck_entries_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_entries_type", "type IN ('credit', 'debit')");
                    table.ForeignKey(
                        name: "fk_entries_accounts",
                        column: x => x.account_id,
                        principalSchema: "ledger",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_entries_reversed_entry",
                        column: x => x.reversed_entry_id,
                        principalSchema: "ledger",
                        principalTable: "entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounts_customer_id",
                schema: "ledger",
                table: "accounts",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_entries_account_id_recorded_at_sequence",
                schema: "ledger",
                table: "entries",
                columns: new[] { "account_id", "recorded_at", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ux_entries_account_id_sequence",
                schema: "ledger",
                table: "entries",
                columns: new[] { "account_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_entries_reversed_entry_id",
                schema: "ledger",
                table: "entries",
                column: "reversed_entry_id",
                unique: true);

            // Lançamento imutável por permissão: a aplicação insere e lê, nunca altera (ADR-0003).
            migrationBuilder.Sql("REVOKE UPDATE ON ledger.entries FROM ledger_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entries",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "accounts",
                schema: "ledger");
        }
    }
}
