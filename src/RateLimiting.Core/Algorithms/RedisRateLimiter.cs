using Microsoft.Extensions.Caching.Distributed;
using RateLimiting.Core.Interfaces;
using RateLimiting.Core.Models;

namespace RateLimiting.Core.Algorithms;

/// <summary>
/// Redis-Backed Sliding Window Counter
/// ─────────────────────────────────────
/// Drop-in replacement for <see cref="SlidingWindowCounterRateLimiter"/> when
/// running multiple API instances behind a load balancer.
///
/// Uses <see cref="IDistributedCache"/> (backed by Redis) so all instances share
/// the same counters. In production, swap IDistributedCache for
/// IConnectionMultiplexer and use a Lua script for atomic INCR + EXPIRE.
///
/// Registration (Program.cs):
/// <code>
///   builder.Services.AddStackExchangeRedisCache(opts =>
///       opts.Configuration = config.GetConnectionString("Redis"));
///   builder.Services.AddSingleton&lt;RedisRateLimiter&gt;();
/// </code>
///
/// appsettings.json:
/// <code>
///   "ConnectionStrings": {
///     "Redis": "your-redis.redis.cache.windows.net:6380,password=xxx,ssl=true"
///   }
/// </code>
/// </summary>
public sealed class RedisRateLimiter : IRateLimiter
{
    private readonly IDistributedCache _cache;

    public RedisRateLimiter(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task<RateLimitResult> IsAllowedAsync(string clientKey, RateLimitPolicy policy)
    {
        var windowSeconds  = (long)policy.Window.TotalSeconds;
        var epochSeconds   = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var currentWindow  = epochSeconds / windowSeconds;
        var windowProgress = (double)(epochSeconds % windowSeconds) / windowSeconds;

        var currentCacheKey = $"rl:{clientKey}:{currentWindow}";
        var prevCacheKey    = $"rl:{clientKey}:{currentWindow - 1}";

        // ── Read both window counters ─────────────────────────────────────────
        // NOTE: In production, replace with a Lua EVALSHA for true atomicity.
        var prevStr    = await _cache.GetStringAsync(prevCacheKey);
        var currentStr = await _cache.GetStringAsync(currentCacheKey);

        var prevCount    = prevStr    is not null ? int.Parse(prevStr)    : 0;
        var currentCount = currentStr is not null ? int.Parse(currentStr) : 0;

        var estimate = prevCount * (1.0 - windowProgress) + currentCount;

        if (estimate < policy.Limit)
        {
            var newCount = currentCount + 1;
            await _cache.SetStringAsync(
                currentCacheKey,
                newCount.ToString(),
                new DistributedCacheEntryOptions
                {
                    // Keep for 2× window so the previous window data is available
                    // after the window rolls over.
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(windowSeconds * 2)
                });

            var remaining = (int)Math.Max(0, policy.Limit - estimate - 1);
            return RateLimitResult.Allow(remaining);
        }

        var retryAfter = TimeSpan.FromSeconds(windowSeconds * (1 - windowProgress) / Math.Max(1, policy.Limit));
        return RateLimitResult.Deny(0, retryAfter, "Redis sliding window limit reached");
    }
}
