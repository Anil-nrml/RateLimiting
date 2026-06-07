using RateLimiting.Core.Models;

namespace RateLimiting.Core.Interfaces;

/// <summary>
/// Contract implemented by all rate-limiting algorithm classes.
/// Each implementation is registered as a singleton so internal state
/// (buckets, counters, logs) persists across HTTP requests.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Determines whether the given client is within their rate limit for the policy.
    /// </summary>
    /// <param name="clientKey">
    ///   Unique key for the client + policy dimension, e.g. "customer-a:per-second".
    ///   Using a compound key lets each policy maintain independent state.
    /// </param>
    /// <param name="policy">The rate-limit policy to evaluate against.</param>
    /// <returns>
    ///   <see cref="RateLimitResult"/> indicating whether the request is allowed,
    ///   how many requests remain, and how long to wait if denied.
    /// </returns>
    Task<RateLimitResult> IsAllowedAsync(string clientKey, RateLimitPolicy policy);
}

/// <summary>
/// Factory that resolves the correct <see cref="IRateLimiter"/> by algorithm.
/// </summary>
public interface IRateLimiterFactory
{
    IRateLimiter GetLimiter(RateLimitAlgorithm algorithm);
}

/// <summary>
/// Provides per-customer <see cref="CustomerRateLimitConfig"/> objects.
/// Implement this to back customer policies from a database, Redis, or config service.
/// </summary>
public interface ICustomerPolicyStore
{
    CustomerRateLimitConfig GetConfig(string customerId);
    bool TryGetConfig(string customerId, out CustomerRateLimitConfig? config);
}
