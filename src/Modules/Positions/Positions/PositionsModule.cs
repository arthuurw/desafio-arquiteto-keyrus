using Microsoft.Extensions.DependencyInjection;

namespace Positions;

// Único tipo público do módulo: ponto de composição usado pelos hosts (ADR-0010).
public static class PositionsModule
{
    public static IServiceCollection AddPositionsModule(this IServiceCollection services) => services;
}
