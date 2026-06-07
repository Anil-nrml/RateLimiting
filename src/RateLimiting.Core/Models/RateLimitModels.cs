namespace RateLimiting.Core.Models;

// ──────────────────────────────────────────────────────────────────────────────
// RateLimitResult
// Returned by every IRateLimiter.IsAllowedAsync call.
// ──────────────────────────────────────────────────────────────────────────────

public sealed record RateLimitResult(
    bool       IsAllowed,
    int        RemainingRequests,
    TimeSpan   RetryAfter,
    string     Reason = "")
{
    /// <summary>Request is within limits.</summary>
    public static RateLimitResult Allow(int remaining, TimeSpan retryAfter = default) =>
        new(true, remaining, retryAfter);

    /// <summary>Request exceeds limit — return 429.</summary>
    public static RateLimitResult Deny(int remaining, TimeSpan retryAfter, string reason = "Rate limit exceeded") =>
        new(false, remaining, retryAfter, reason);
}

// ──────────────────────────────────────────────────────────────────────────────
// RateLimitPolicy
// Defines a single rate-limit rule: N requests per Window using Algorithm.
// ──────────────────────────────────────────────────────────────────────────────

public sealed record RateLimitPolicy(
    string              Name,
    int                 Limit,
    TimeSpan            Window,
    int                 BurstLimit = 0,
    RateLimitAlgorithm  Algorithm  = RateLimitAlgorithm.SlidingWindowCounter)
{
    /// <summary>Effective burst capacity — falls back to Limit when not specified.</summary>
    public int EffectiveBurst => BurstLimit > 0 ? BurstLimit : Limit;
}

// ──────────────────────────────────────────────────────────────────────────────
// CustomerRateLimitConfig
// Bundles one or more policies for a specific customer/tenant.
// All policies must pass (AND semantics).
// ──────────────────────────────────────────────────────────────────────────────

public sealed record CustomerRateLimitConfig(
    string          CustomerId,
    RateLimitPolicy[] Policies)
{
    /// <summary>Primary policy used for response-header values.</summary>
    public RateLimitPolicy? PrimaryPolicy => Policies.Length > 0 ? Policies[0] : null;
}

// ──────────────────────────────────────────────────────────────────────────────
// RateLimitAlgorithm
// ──────────────────────────────────────────────────────────────────────────────

public enum RateLimitAlgorithm
{
    TokenBucket,
    LeakyBucket,
    FixedWindowCounter,
    SlidingWindowLog,
    SlidingWindowCounter
}
