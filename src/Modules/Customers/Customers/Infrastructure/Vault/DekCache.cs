using System.Security.Cryptography;
using BuildingBlocks.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Customers.Infrastructure.Vault;

// DEKs lidas do cofre há menos de `Ttl` (5 min por padrão, ADR-0016), até `Capacity` clientes. Cada leitura recebe uma
// cópia; a cópia do cache é zerada quando sai dele.
internal sealed class DekCache : IDisposable
{
    internal const string TtlSetting = "Customers:DekCache:Ttl";
    internal const int Capacity = 10_000;
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxTtl = TimeSpan.FromHours(1);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = Capacity });

    // A cópia na leitura e o zero na saída do cache não se cruzam: sem a trava, quem leu a referência um instante antes
    // da substituição copiaria uma DEK já zerada.
    private readonly Lock _gate = new();

    internal DekCache(TimeSpan ttl) => Ttl = ttl;

    internal TimeSpan Ttl { get; }

    internal static TimeSpan ReadTtl(IConfiguration configuration) =>
        TimeSpanSetting.Read(configuration, TtlSetting, DefaultTtl, MinTtl, MaxTtl);

    internal byte[]? TryGet(Guid customerId)
    {
        lock (_gate)
        {
            return _cache.TryGetValue(customerId, out byte[]? dek) && dek is not null ? (byte[])dek.Clone() : null;
        }
    }

    // Prazo contado da leitura no cofre, não da última consulta: a DEK não fica na memória além disso.
    internal void Set(Guid customerId, byte[] dek)
    {
        var options = new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Ttl };
        options.RegisterPostEvictionCallback((_, value, _, _) =>
        {
            lock (_gate)
            {
                CryptographicOperations.ZeroMemory((byte[])value!);
            }
        });
        _cache.Set(customerId, (byte[])dek.Clone(), options);
    }

    public void Dispose() => _cache.Dispose();
}

// DEK pelo cache e, na falta, pelo cofre; a lida do cofre entra no cache.
internal sealed class CustomerKeys(IKeyStore store, DekCache cache)
{
    internal async Task<byte[]> ReadAsync(Guid customerId, CancellationToken cancellationToken)
    {
        if (cache.TryGet(customerId) is { } cached)
        {
            return cached;
        }

        var dek = await store.ReadAsync(customerId, cancellationToken);
        cache.Set(customerId, dek);
        return dek;
    }
}
