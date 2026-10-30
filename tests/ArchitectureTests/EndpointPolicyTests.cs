extern alias ApiHost;

using ArchitectureTests.Rules;
using BuildingBlocks.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ArchitectureTests;

public sealed class EndpointPolicyTests
{
    [Fact]
    public async Task RuleRejectsFixtureEndpointWithoutPolicy()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddAuthorization();
        await using var fixture = builder.Build();
        fixture.MapGet("/scoped", () => "ok").RequireScope("ledger.debits.write");
        fixture.MapGet("/anonymous", () => "ok").AllowAnonymous();
        fixture.MapGet("/forgotten", () => "ok");

        var violations = EndpointPolicyRules.Violations(((IEndpointRouteBuilder)fixture).DataSources.SelectMany(source => source.Endpoints));

        violations.ShouldBe(["HTTP: GET /forgotten"]);
    }

    [Fact]
    public async Task ApiEndpointsDeclarePolicy()
    {
        await using var api = new WebApplicationFactory<ApiHost::Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Development"));

        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        endpoints.ShouldNotBeEmpty();
        EndpointPolicyRules.Violations(endpoints).ShouldBeEmpty();
    }
}
