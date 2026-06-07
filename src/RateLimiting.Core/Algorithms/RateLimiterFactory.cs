using RateLimiting.Core.Interfaces;
using RateLimiting.Core.Models;

namespace RateLimiting.Core.Algorithms;

/// <summary>
/// Resolves the correct <see cref="IRateLimiter"/> implementation for a given
/// <see cref="RateLimitAlgorithm"/> value.
///
/// All algorithm implementations are injected at construction time (singleton scope),
/// so their internal state persists across the lifetime of the application.
/// </summary>
public sealed class RateLimiterFactory : IRateLimiterFactory
{
    private readonly IReadOnlyDictionary<RateLimitAlgorithm, IRateLimiter> _limiters;

    public RateLimiterFactory(
        TokenBucketRateLimiter          tokenBucket,
        LeakyBucketRateLimiter          leakyBucket,
        FixedWindowCounterRateLimiter   fixedWindow,
        SlidingWindowLogRateLimiter     slidingLog,
        SlidingWindowCounterRateLimiter slidingCounter)
    {
        _limiters = new Dictionary<RateLimitAlgorithm, IRateLimiter>
        {
            [RateLimitAlgorithm.TokenBucket]          = tokenBucket,
            [RateLimitAlgorithm.LeakyBucket]          = leakyBucket,
            [RateLimitAlgorithm.FixedWindowCounter]   = fixedWindow,
            [RateLimitAlgorithm.SlidingWindowLog]     = slidingLog,
            [RateLimitAlgorithm.SlidingWindowCounter] = slidingCounter,
        };
    }

    /// <inheritdoc />
    public IRateLimiter GetLimiter(RateLimitAlgorithm algorithm) =>
        _limiters.TryGetValue(algorithm, out var limiter)
            ? limiter
            : throw new ArgumentOutOfRangeException(
                nameof(algorithm),
                algorithm,
                $"No rate limiter registered for algorithm '{algorithm}'.");
}
