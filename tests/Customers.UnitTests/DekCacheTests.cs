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

    // A DEK substituída é zerada ao sair do cache; quem a leu um instante antes ainda recebe a cópia inteira.
    [Fact]
    public async Task ReadDuringReplacementNeverGetsAZeroedKey()
    {
        const int Readers = 4;
        const int Replacements = 20_000;
        using var cache = new DekCache(TimeSpan.FromMinutes(5));
        var customer = Guid.NewGuid();
        var dek = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        cache.Set(customer, dek);
        using var start = new Barrier(Readers + 1);
        var done = 0;

        var readers = Enumerable.Range(0, Readers).Select(_ => Task.Run(
            () =>
            {
                var (reads, damaged) = (0, 0);
                start.SignalAndWait(TestContext.Current.CancellationToken);
                while (Volatile.Read(ref done) == 0)
                {
                    reads++;
                    if (!cache.TryGet(customer).ShouldNotBeNull().AsSpan().SequenceEqual(dek))
                    {
                        damaged++;
                    }
                }

                return (reads, damaged);
            },
            TestContext.Current.CancellationToken)).ToArray();
        var writer = Task.Run(
            () =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                for (var replacement = 0; replacement < Replacements; replacement++)
                {
                    cache.Set(customer, dek);
                }

                Volatile.Write(ref done, 1);
            },
            TestContext.Current.CancellationToken);
        await writer;
        var results = await Task.WhenAll(readers);

        results.Sum(result => result.reads).ShouldBeGreaterThan(0);
        results.Sum(result => result.damaged).ShouldBe(0);
    }
}
