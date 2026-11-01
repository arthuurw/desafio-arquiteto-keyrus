using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace BuildingBlocks.Configuration;

// Intervalo lido da configuração e checado na subida: inválido, o host cairia só no primeiro uso, já no ar.
public static class TimeSpanSetting
{
    public static TimeSpan Read(IConfiguration configuration, string key, TimeSpan defaultValue, TimeSpan min, TimeSpan max)
    {
        var configured = configuration[key];
        if (configured is null)
        {
            return defaultValue;
        }

        if (!TimeSpan.TryParse(configured, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new InvalidOperationException($"{key} must be a time span between {min:c} and {max:c}; got '{configured}'.");
        }

        return value;
    }
}
