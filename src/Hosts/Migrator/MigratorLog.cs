using Microsoft.Extensions.Logging;

namespace Migrator;

internal static partial class MigratorLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "{Module}: no pending migrations")]
    internal static partial void NoPendingMigrations(this ILogger logger, string module);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Module}: applied migration {Migration}")]
    internal static partial void AppliedMigration(this ILogger logger, string module, string migration);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Migration failed")]
    internal static partial void MigrationFailed(this ILogger logger, Exception exception);
}
