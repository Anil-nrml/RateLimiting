using FluentAssertions;
using RateLimiting.Core.Algorithms;
using RateLimiting.Core.Models;
using Xunit;

namespace RateLimiting.Tests.Algorithms;

// ──────────────────────────────────────────────────────────────────────────────
// Shared test helpers
// ──────────────────────────────────────────────────────────────────────────────

public static class PolicyFactory
{
    public static RateLimitPolicy PerSecond(int limit, RateLimitAlgorithm algo, int burst = 0) =>
        new("test-policy", limit, TimeSpan.FromSeconds(1), burst, algo);

    public static RateLimitPolicy PerMinute(int limit, RateLimitAlgorithm algo) =>
        new("test-policy", limit, TimeSpan.FromMinutes(1), 0, algo);
}

// ──────────────────────────────────────────────────────────────────────────────
// Token Bucket Tests
// ──────────────────────────────────────────────────────────────────────────────

public sealed class TokenBucketRateLimiterTests
{
    private readonly TokenBucketRateLimiter _sut = new();
    private readonly RateLimitPolicy _policy = PolicyFactory.PerSecond(5, RateLimitAlgorithm.TokenBucket, burst: 5);

    [Fact]
    public async Task Allows_requests_up_to_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            var result = await _sut.IsAllowedAsync("client-1", _policy);
            result.IsAllowed.Should().BeTrue(because: $"request {i + 1} should be within limit");
        }
    }

    [Fact]
    public async Task Denies_request_when_tokens_exhausted()
    {
        var policy  = PolicyFactory.PerSecond(3, RateLimitAlgorithm.TokenBucket, burst: 3);
        var clientId = "client-token-exhaust";

        for (var i = 0; i < 3; i++)
            await _sut.IsAllowedAsync(clientId, policy);

        var result = await _sut.IsAllowedAsync(clientId, policy);
        result.IsAllowed.Should().BeFalse();
        result.RetryAfter.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task Remaining_decrements_with_each_request()
    {
        var policy   = PolicyFactory.PerSecond(5, RateLimitAlgorithm.TokenBucket, burst: 5);
        var clientId = "client-remaining";

        var r1 = await _sut.IsAllowedAsync(clientId, policy);
        var r2 = await _sut.IsAllowedAsync(clientId, policy);

        r2.RemainingRequests.Should().BeLessThan(r1.RemainingRequests);
    }

    [Fact]
    public async Task Different_clients_have_independent_buckets()
    {
        var policy = PolicyFactory.PerSecond(2, RateLimitAlgorithm.TokenBucket, burst: 2);

        // Exhaust client A
        await _sut.IsAllowedAsync("clientA", policy);
        await _sut.IsAllowedAsync("clientA", policy);
        var deniedA = await _sut.IsAllowedAsync("clientA", policy);

        // Client B should still be allowed
        var allowedB = await _sut.IsAllowedAsync("clientB", policy);

        deniedA.IsAllowed.Should().BeFalse();
        allowedB.IsAllowed.Should().BeTrue();
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// Leaky Bucket Tests
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LeakyBucketRateLimiterTests
{
    private readonly LeakyBucketRateLimiter _sut = new();

    [Fact]
    public async Task Allows_requests_within_capacity()
    {
        var policy   = PolicyFactory.PerSecond(5, RateLimitAlgorithm.LeakyBucket, burst: 5);
        var clientId = "leaky-allow";

        for (var i = 0; i < 5; i++)
        {
            var r = await _sut.IsAllowedAsync(clientId, policy);
            r.IsAllowed.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Denies_when_bucket_full()
    {
        var policy   = PolicyFactory.PerSecond(3, RateLimitAlgorithm.LeakyBucket, burst: 3);
        var clientId = "leaky-full";

        for (var i = 0; i < 3; i++)
            await _sut.IsAllowedAsync(clientId, policy);

        var result = await _sut.IsAllowedAsync(clientId, policy);
        result.IsAllowed.Should().BeFalse();
        result.RetryAfter.Should().BeGreaterThan(TimeSpan.Zero);
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// Fixed Window Counter Tests
// ──────────────────────────────────────────────────────────────────────────────

public sealed class FixedWindowCounterRateLimiterTests
{
    private readonly FixedWindowCounterRateLimiter _sut = new();

    [Fact]
    public async Task Allows_up_to_limit_in_window()
    {
        var policy   = PolicyFactory.PerMinute(10, RateLimitAlgorithm.FixedWindowCounter);
        var clientId = $"fw-allow-{Guid.NewGuid()}";

        for (var i = 0; i < 10; i++)
        {
            var r = await _sut.IsAllowedAsync(clientId, policy);
            r.IsAllowed.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Denies_over_limit_in_window()
    {
        var policy   = PolicyFactory.PerMinute(3, RateLimitAlgorithm.FixedWindowCounter);
        var clientId = $"fw-deny-{Guid.NewGuid()}";

        for (var i = 0; i < 3; i++)
            await _sut.IsAllowedAsync(clientId, policy);

        var result = await _sut.IsAllowedAsync(clientId, policy);
        result.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Returns_correct_remaining_count()
    {
        var policy   = PolicyFactory.PerMinute(5, RateLimitAlgorithm.FixedWindowCounter);
        var clientId = $"fw-remaining-{Guid.NewGuid()}";

        var r1 = await _sut.IsAllowedAsync(clientId, policy);
        r1.RemainingRequests.Should().Be(4);

        var r2 = await _sut.IsAllowedAsync(clientId, policy);
        r2.RemainingRequests.Should().Be(3);
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// Sliding Window Log Tests
// ──────────────────────────────────────────────────────────────────────────────

public sealed class SlidingWindowLogRateLimiterTests
{
    private readonly SlidingWindowLogRateLimiter _sut = new();

    [Fact]
    public async Task Allows_up_to_limit()
    {
        var policy   = PolicyFactory.PerSecond(5, RateLimitAlgorithm.SlidingWindowLog);
        var clientId = $"swl-allow-{Guid.NewGuid()}";

        for (var i = 0; i < 5; i++)
        {
            var r = await _sut.IsAllowedAsync(clientId, policy);
            r.IsAllowed.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Denies_over_limit_and_provides_retry_after()
    {
        var policy   = PolicyFactory.PerSecond(3, RateLimitAlgorithm.SlidingWindowLog);
        var clientId = $"swl-deny-{Guid.NewGuid()}";

        for (var i = 0; i < 3; i++)
            await _sut.IsAllowedAsync(clientId, policy);

        var result = await _sut.IsAllowedAsync(clientId, policy);
        result.IsAllowed.Should().BeFalse();
        result.RetryAfter.Should().BeGreaterThan(TimeSpan.Zero);
        result.RetryAfter.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(1));
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// Sliding Window Counter Tests
// ──────────────────────────────────────────────────────────────────────────────

public sealed class SlidingWindowCounterRateLimiterTests
{
    private readonly SlidingWindowCounterRateLimiter _sut = new();

    [Fact]
    public async Task Allows_up_to_limit()
    {
        var policy   = PolicyFactory.PerSecond(10, RateLimitAlgorithm.SlidingWindowCounter);
        var clientId = $"swc-allow-{Guid.NewGuid()}";

        for (var i = 0; i < 10; i++)
        {
            var r = await _sut.IsAllowedAsync(clientId, policy);
            r.IsAllowed.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Denies_after_limit_reached()
    {
        var policy   = PolicyFactory.PerSecond(5, RateLimitAlgorithm.SlidingWindowCounter);
        var clientId = $"swc-deny-{Guid.NewGuid()}";

        for (var i = 0; i < 5; i++)
            await _sut.IsAllowedAsync(clientId, policy);

        var result = await _sut.IsAllowedAsync(clientId, policy);
        result.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Independent_clients_dont_interfere()
    {
        var policy = PolicyFactory.PerSecond(2, RateLimitAlgorithm.SlidingWindowCounter);

        await _sut.IsAllowedAsync($"swc-a-{Guid.NewGuid()}", policy);
        await _sut.IsAllowedAsync($"swc-a-{Guid.NewGuid()}", policy);

        var otherClient = await _sut.IsAllowedAsync($"swc-b-{Guid.NewGuid()}", policy);
        otherClient.IsAllowed.Should().BeTrue();
    }
}
