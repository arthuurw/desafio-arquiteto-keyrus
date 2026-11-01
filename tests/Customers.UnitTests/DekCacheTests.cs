using Customers.Infrastructure.Vault;
using Microsoft.Extensions.Configuration;

namespace Customers.UnitTests;

public sealed class DekCacheTests
{
    [Fact]
    public void DefaultTtlIsFiveMinutes()
    {
        var unset = new ConfigurationBuilder().Build();
        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Customers:DekCache:Ttl"] = "00:00:03" })
            .Build();

        using var cache = new DekCache(DekCache.ReadTtl(unset));

        cache.Ttl.ShouldBe(TimeSpan.FromMinutes(5));
        DekCache.ReadTtl(configured).ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void EachReadGetsItsOwnCopy()
    {
        using var cache = new DekCache(TimeSpan.FromMinutes(5));
        var customer = Guid.NewGuid();
        var dek = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

        cache.Set(customer, dek);
        var first = cache.TryGet(customer).ShouldNotBeNull();
        Array.Clear(first);

        cache.TryGet(customer).ShouldBe(dek);
        cache.TryGet(Guid.NewGuid()).ShouldBeNull();
    }
}
