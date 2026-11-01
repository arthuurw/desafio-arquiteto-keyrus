using System.Net;
using System.Text;
using Customers.Infrastructure.Vault;

namespace Customers.UnitTests;

public sealed class OpenBaoClientTests
{
    // Token de vida mais curta que a margem de renovação de 1 min: segue valendo até a metade da vida, sem um login
    // a cada chamada.
    [Fact]
    public async Task ShortLivedTokenIsReusedWithinItsLife()
    {
        using var vault = new CountingVault(leaseSeconds: 30);
        using var client = new OpenBaoClient(
            new HttpClient(vault),
            new OpenBaoSettings(new Uri("http://vault.test"), "role", "secret"),
            TimeProvider.System);

        await client.SendAsync(HttpMethod.Get, "customers/data/dek/one", null, TestContext.Current.CancellationToken);
        await client.SendAsync(HttpMethod.Get, "customers/data/dek/two", null, TestContext.Current.CancellationToken);

        vault.Logins.ShouldBe(1);
        vault.Reads.ShouldBe(2);
    }

    // Cofre escrito à mão: responde ao login do AppRole com o TTL pedido e a toda leitura com um corpo vazio.
    private sealed class CountingVault(int leaseSeconds) : HttpMessageHandler
    {
        internal int Logins { get; private set; }

        internal int Reads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var login = request.RequestUri!.AbsolutePath == "/v1/auth/approle/login";
            if (login)
            {
                Logins++;
            }
            else
            {
                Reads++;
            }

            var body = login
                ? $$$"""{"auth":{"client_token":"token-{{{Logins}}}","lease_duration":{{{leaseSeconds}}}}}"""
                : """{"data":{}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
