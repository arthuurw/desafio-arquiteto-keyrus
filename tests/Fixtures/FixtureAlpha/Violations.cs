using FixtureAlpha.Infrastructure;
using FixtureBeta;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using MongoDB.Driver;
using Npgsql;
using NpgsqlTypes;

namespace FixtureAlpha
{
    // Violação de visibilidade: tipo público além de FixtureAlphaModule.
    public sealed class ExtraPublicType
    {
        public int Value { get; set; }
    }

    // Violação de fronteira: usa um tipo interno de outro módulo.
    internal sealed class UsesBetaInternals
    {
        internal BetaInternalType? Beta { get; set; }
    }
}

namespace FixtureAlpha.Infrastructure
{
    internal sealed class AlphaRepository
    {
        internal int Value { get; set; }
    }
}

// Violações de domínio: uma dependência proibida por tipo.
namespace FixtureAlpha.Domain
{
    internal sealed class DependsOnEfCore
    {
        internal DbContext? Context { get; set; }
    }

    internal sealed class DependsOnNpgsql
    {
        internal NpgsqlConnection? Connection { get; set; }
    }

    internal sealed class DependsOnMongoDb
    {
        internal IMongoClient? Client { get; set; }
    }

    internal sealed class DependsOnAspNetCore
    {
        internal HttpContext? Context { get; set; }
    }

    internal sealed class DependsOnInfrastructure
    {
        internal AlphaRepository? Repository { get; set; }
    }

    // Namespaces fora do prefixo do pacote, mas dentro dos assemblies proibidos.
    internal sealed class DependsOnNpgsqlTypes
    {
        internal NpgsqlRange<int> Range { get; set; }
    }

    internal sealed class DependsOnHttpHeaders
    {
        internal MediaTypeHeaderValue? MediaType { get; set; }
    }
}
