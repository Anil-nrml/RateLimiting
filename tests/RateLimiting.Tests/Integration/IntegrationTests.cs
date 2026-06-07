using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RateLimiting.Tests.Integration;

/// <summary>
/// Integration tests that spin up the full ASP.NET pipeline using
/// <see cref="WebApplicationFactory{TEntryPoint}"/> and hit real endpoints.
///
/// Run with:  dotnet test
/// </summary>
public sealed class RateLimitIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RateLimitIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // ── Ping endpoint ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Ping_returns_200_for_customer_a_within_limit()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Customer-ID", "customer-a");

        var response = await client.GetAsync("/api/demo/ping");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ping_returns_200_for_customer_b_within_limit()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Customer-ID", "customer-b");

        var response = await client.GetAsync("/api/demo/ping");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Rate-limit headers ────────────────────────────────────────────────────

    [Fact]
    public async Task Response_contains_rate_limit_headers()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Customer-ID", "customer-a");

        var response = await client.GetAsync("/api/demo/ping");

        response.Headers.Should().ContainKey("X-RateLimit-Limit");
        response.Headers.Should().ContainKey("X-RateLimit-Remaining");
        response.Headers.Should().ContainKey("X-RateLimit-Reset");
        response.Headers.Should().ContainKey("X-RateLimit-Algorithm");
    }

    [Fact]
    public async Task Customer_a_has_higher_limit_than_customer_b()
    {
        var clientA = _factory.CreateClient();
        clientA.DefaultRequestHeaders.Add("X-Customer-ID", "customer-a");

        var clientB = _factory.CreateClient();
        clientB.DefaultRequestHeaders.Add("X-Customer-ID", "customer-b");

        var responseA = await clientA.GetAsync("/api/demo/ping");
        var responseB = await clientB.GetAsync("/api/demo/ping");

        var limitA = int.Parse(responseA.Headers.GetValues("X-RateLimit-Limit").First());
        var limitB = int.Parse(responseB.Headers.GetValues("X-RateLimit-Limit").First());

        limitA.Should().BeGreaterThan(limitB);
    }

    // ── Policy endpoint ───────────────────────────────────────────────────────

    [Fact]
    public async Task Policy_endpoint_returns_customer_a_policies()
    {
        var client   = _factory.CreateClient();
        var response = await client.GetAsync("/api/demo/policy/customer-a");
        var content  = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("customer-a");
        content.Should().Contain("per-second");
        content.Should().Contain("per-hour");
    }

    [Fact]
    public async Task Policy_endpoint_returns_customer_b_policies()
    {
        var client   = _factory.CreateClient();
        var response = await client.GetAsync("/api/demo/policy/customer-b");
        var content  = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("customer-b");
    }

    // ── 429 on burst ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Anonymous_caller_gets_429_after_60_requests_per_minute()
    {
        // Anonymous falls to the default 60/min policy (FixedWindowCounter).
        // We spin up a factory with a fresh pipeline so counters aren't shared
        // with other tests.
        var factory = _factory.WithWebHostBuilder(_ => { });
        var client  = factory.CreateClient();
        // No X-Customer-ID → falls through to IP → "default" policy: 60/min

        HttpResponseMessage? lastResponse = null;
        var deniedCount = 0;

        for (var i = 0; i < 65; i++)
        {
            lastResponse = await client.GetAsync("/api/demo/ping");
            if (lastResponse.StatusCode == HttpStatusCode.TooManyRequests)
                deniedCount++;
        }

        // At least the last few requests should be denied.
        deniedCount.Should().BeGreaterThan(0);
        lastResponse!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        lastResponse.Headers.Should().ContainKey("Retry-After");
    }
}
