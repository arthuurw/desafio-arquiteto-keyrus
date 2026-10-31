using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AppRoleWithoutMigrationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // O histórico de migração é só do migrator: uma linha inserida ali faria uma migração ser pulada.
            migrationBuilder.Sql("""REVOKE ALL ON customers."__EFMigrationsHistory" FROM customers_app;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""GRANT SELECT, INSERT, UPDATE ON customers."__EFMigrationsHistory" TO customers_app;""");
        }
    }
}
