using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Customers.Infrastructure.Persistence;

internal sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options) : DbContext(options)
{
    internal const string Schema = "customers";

    internal const string CpfBlindIndexUnique = "ux_customers_cpf_blind_index";

    internal DbSet<CustomerRow> Customers => Set<CustomerRow>();

    // Histórico de migração no schema do módulo, nunca em public (ADR-0038).
    internal static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, Schema);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<CustomerRow>(customer =>
        {
            customer.ToTable("customers");
            customer.HasKey(c => c.Id).HasName("pk_customers");
            customer.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            customer.Property(c => c.Name).HasColumnName("name");
            customer.Property(c => c.Cpf).HasColumnName("cpf");
            customer.Property(c => c.BirthDate).HasColumnName("birth_date");
            customer.Property(c => c.Email).HasColumnName("email");
            customer.Property(c => c.CpfBlindIndex).HasColumnName("cpf_blind_index");
            customer.Property(c => c.CpfBlindIndexVersion).HasColumnName("cpf_blind_index_version");
            customer.Property(c => c.RegisteredAt).HasColumnName("registered_at").HasDefaultValueSql("now()");
            // Um cliente por CPF, também sob concorrência: a consulta prévia recusa o caso comum, o índice a corrida.
            customer.HasIndex(c => c.CpfBlindIndex).IsUnique().HasDatabaseName(CpfBlindIndexUnique);
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
