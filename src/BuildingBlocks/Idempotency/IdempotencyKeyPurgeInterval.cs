using BuildingBlocks.Configuration;
using Microsoft.Extensions.Configuration;

namespace BuildingBlocks.Idempotency;

// Intervalo da limpeza de chaves de idempotência, o mesmo para a rotina de cada módulo (ADR-0037).
public static class IdempotencyKeyPurgeInterval
{
    public const string Setting = "Idempotency:PurgeInterval";

    public static TimeSpan Read(IConfiguration configuration) =>
        TimeSpanSetting.Read(configuration, Setting, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1), TimeSpan.FromDays(1));
}
