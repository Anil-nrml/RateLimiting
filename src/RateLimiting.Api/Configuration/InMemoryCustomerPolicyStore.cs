using RateLimiting.Core.Interfaces;
using RateLimiting.Core.Models;

namespace RateLimiting.Api.Configuration;

/// <summary>
/// In-memory implementation of <see cref="ICustomerPolicyStore"/>.
///
/// Defines rate-limit policies per customer:
///   • Customer A (Enterprise)  — 1 000 req/s + 2 000 000 req/hour
///   • Customer B (Standard)    — 100 req/s   +   200 000 req/hour
///   • Default  (Anonymous/IP)  —  60 req/min
///
/// Multiple policies per customer use AND semantics:
///   ALL must pass — the first failure short-circuits with 429.
///
/// Production note:
///   Replace this class (or extend it) to load policies from a database,
///   Redis, Azure App Configuration, or any config service.
///   The rest of the pipeline never touches this class directly — it depends
///   only on <see cref="ICustomerPolicyStore"/>.
/// </summary>
public sealed class InMemoryCustomerPolicyStore : ICustomerPolicyStore
{
    private readonly IReadOnlyDictionary<string, CustomerRateLimitConfig> _configs;

    public InMemoryCustomerPolicyStore()
    {
        _configs = BuildConfigs();
    }

    /// <inheritdoc />
    public CustomerRateLimitConfig GetConfig(string customerId)
    {
        if (_configs.TryGetValue(customerId.ToLowerInvariant(), out var config))
            return config;

        return _configs["default"];
    }

    /// <inheritdoc />
    public bool TryGetConfig(string customerId, out CustomerRateLimitConfig? config) =>
        _configs.TryGetValue(customerId.ToLowerInvariant(), out config);

    // ── Policy definitions ────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, CustomerRateLimitConfig> BuildConfigs() =>
        new Dictionary<string, CustomerRateLimitConfig>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Customer A: Enterprise ─────────────────────────────────────────
            // 1 000 req/sec with a burst ceiling of 1 500 (Token Bucket)
            // 2 000 000 req/hour guard (Sliding Window Counter)
            ["customer-a"] = new CustomerRateLimitConfig(
                CustomerId: "customer-a",
                Policies:
                [
                    new RateLimitPolicy(
                        Name:       "per-second",
                        Limit:      1_000,
                        Window:     TimeSpan.FromSeconds(1),
                        BurstLimit: 1_500,
                        Algorithm:  RateLimitAlgorithm.TokenBucket),

                    new RateLimitPolicy(
                        Name:       "per-hour",
                        Limit:      2_000_000,
                        Window:     TimeSpan.FromHours(1),
                        Algorithm:  RateLimitAlgorithm.SlidingWindowCounter),
                ]),

            // ── Customer B: Standard ───────────────────────────────────────────
            // 100 req/sec with a burst ceiling of 150 (Token Bucket)
            // 200 000 req/hour guard (Sliding Window Counter)
            ["customer-b"] = new CustomerRateLimitConfig(
                CustomerId: "customer-b",
                Policies:
                [
                    new RateLimitPolicy(
                        Name:       "per-second",
                        Limit:      100,
                        Window:     TimeSpan.FromSeconds(1),
                        BurstLimit: 150,
                        Algorithm:  RateLimitAlgorithm.TokenBucket),

                    new RateLimitPolicy(
                        Name:       "per-hour",
                        Limit:      200_000,
                        Window:     TimeSpan.FromHours(1),
                        Algorithm:  RateLimitAlgorithm.SlidingWindowCounter),
                ]),

            // ── Default: Anonymous / IP-based ──────────────────────────────────
            // 60 req/min — conservative fallback for unknown callers
            ["default"] = new CustomerRateLimitConfig(
                CustomerId: "default",
                Policies:
                [
                    new RateLimitPolicy(
                        Name:      "per-minute",
                        Limit:     60,
                        Window:    TimeSpan.FromMinutes(1),
                        Algorithm: RateLimitAlgorithm.FixedWindowCounter),
                ]),
        };
}
