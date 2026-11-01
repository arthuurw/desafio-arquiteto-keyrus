using Ledger.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Ledger.Infrastructure.Persistence;

internal sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    internal const string Schema = "ledger";

    // Nunca o tipo `money` do PostgreSQL, que depende da localidade do servidor (ADR-0018).
    private const string MoneyColumn = "numeric(19,4)";

    private static readonly ValueConverter<Money, decimal> MoneyConverter = new(money => money.Amount, amount => Money.Of(amount, Money.Brl));

    private static readonly ValueConverter<EntryType, string> EntryTypeConverter = new(
        type => type == EntryType.Credit ? "credit" : "debit",
        value => value == "credit" ? EntryType.Credit : EntryType.Debit);

    internal DbSet<Account> Accounts => Set<Account>();

    internal DbSet<Entry> Entries => Set<Entry>();

    // Histórico de migração no schema do módulo, nunca em public (ADR-0038).
    internal static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, Schema);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Account>(account =>
        {
            // O CHECK de saldo é a segunda barreira do limite, depois do agregado (ADR-0005).
            account.ToTable("accounts", table =>
            {
                table.HasCheckConstraint("ck_accounts_balance_within_limit", "balance >= -credit_limit");
                table.HasCheckConstraint("ck_accounts_credit_limit_not_negative", "credit_limit >= 0");
            });
            account.HasKey(a => a.Id).HasName("pk_accounts");
            account.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            account.Property(a => a.CustomerId).HasColumnName("customer_id");
            account.Property(a => a.Balance).HasColumnName("balance").HasColumnType(MoneyColumn)
                .HasConversion(MoneyConverter).HasDefaultValueSql("0");
            account.Property(a => a.CreditLimit).HasColumnName("credit_limit").HasColumnType(MoneyColumn)
                .HasConversion(MoneyConverter).HasDefaultValueSql("0");
            account.Property(a => a.Version).HasColumnName("version").HasDefaultValueSql("0");
            account.Property(a => a.LastRecordedAt).HasColumnName("last_recorded_at");
            // Relógio do banco, o mesmo do `recorded_at`: a abertura nunca fica depois do primeiro lançamento.
            account.Property(a => a.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("clock_timestamp()");
            account.HasIndex(a => a.CustomerId).HasDatabaseName("ix_accounts_customer_id");
        });

        modelBuilder.Entity<Entry>(entry =>
        {
            entry.ToTable("entries", table =>
            {
                table.HasCheckConstraint("ck_entries_type", "type IN ('credit', 'debit')");
                table.HasCheckConstraint("ck_entries_amount_positive", "amount > 0");
            });
            entry.HasKey(e => e.Id).HasName("pk_entries");
            entry.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entry.Property(e => e.AccountId).HasColumnName("account_id");
            entry.Property(e => e.Sequence).HasColumnName("sequence");
            entry.Property(e => e.Type).HasColumnName("type").HasConversion(EntryTypeConverter);
            entry.Property(e => e.Amount).HasColumnName("amount").HasColumnType(MoneyColumn).HasConversion(MoneyConverter);
            entry.Property(e => e.BalanceAfter).HasColumnName("balance_after").HasColumnType(MoneyColumn).HasConversion(MoneyConverter);
            entry.Property(e => e.RecordedAt).HasColumnName("recorded_at");
            entry.Property(e => e.Category).HasColumnName("category");
            entry.Property(e => e.ExternalReference).HasColumnName("external_reference");
            entry.Property(e => e.ClientId).HasColumnName("client_id");
            entry.Property(e => e.ReversedEntryId).HasColumnName("reversed_entry_id");

            entry.HasOne<Account>().WithMany().HasForeignKey(e => e.AccountId)
                .HasConstraintName("fk_entries_accounts").OnDelete(DeleteBehavior.Restrict);
            // Um estorno por lançamento: a unicidade vale também sob concorrência.
            entry.HasOne<Entry>().WithOne().HasForeignKey<Entry>(e => e.ReversedEntryId)
                .HasConstraintName("fk_entries_reversed_entry").OnDelete(DeleteBehavior.Restrict);
            entry.HasIndex(e => e.ReversedEntryId).IsUnique().HasDatabaseName("ux_entries_reversed_entry_id");
            entry.HasIndex(e => new { e.AccountId, e.Sequence }).IsUnique().HasDatabaseName("ux_entries_account_id_sequence");
            // Barreira final contra lançamento duplicado, independente da chave de idempotência (ADR-0037).
            entry.HasIndex(e => new { e.ClientId, e.ExternalReference }).IsUnique().HasDatabaseName(ExternalReferences.UniqueIndex);
            // Atende a consulta de saldo em um momento: último lançamento até T, desempate por sequência.
            entry.HasIndex(e => new { e.AccountId, e.RecordedAt, e.Sequence }).HasDatabaseName("ix_entries_account_id_recorded_at_sequence");

            entry.Ignore(e => e.IsReversal);
        });

        // Escopo por client: chaves de sistemas diferentes nunca colidem (ADR-0037).
        modelBuilder.Entity<IdempotencyRecord>(key =>
        {
            key.ToTable("idempotency_keys");
            key.HasKey(k => new { k.ClientId, k.Key }).HasName("pk_idempotency_keys");
            key.Property(k => k.ClientId).HasColumnName("client_id");
            key.Property(k => k.Key).HasColumnName("idempotency_key");
            key.Property(k => k.RequestHash).HasColumnName("request_hash");
            key.Property(k => k.ResponseStatus).HasColumnName("response_status");
            key.Property(k => k.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb");
            key.Property(k => k.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            // Atende a limpeza por idade no Worker.
            key.HasIndex(k => k.CreatedAt).HasDatabaseName("ix_idempotency_keys_created_at");
        });
    }
}
