using System.Collections.Concurrent;
using RateLimiting.Core.Interfaces;
using RateLimiting.Core.Models;

namespace RateLimiting.Core.Algorithms;

/// <summary>
/// Sliding Window Log Rate Limiter
/// ────────────────────────────────
/// Behaviour:
///   • Stores the timestamp of every request in a sorted log per client.
///   • On each request, timestamps older than <c>Window</c> are purged.
///   • The remaining count is compared against <c>Limit</c>.
///
/// Characteristics:
///   ✓ Perfectly accurate — no boundary burst problem
///   ✓ RetryAfter is precise: exactly when the oldest entry expires
///   ✗ O(n) memory per client — log grows with request volume
///   ✗ Not suitable for very high-traffic clients (>10k req/s per key)
///
/// Best for: High-accuracy requirements, billing-sensitive APIs, low-to-medium traffic.
/// </summary>
public sealed class SlidingWindowLogRateLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<string, RequestLog> _logs = new();

    public Task<RateLimitResult> IsAllowedAsync(string clientKey, RateLimitPolicy policy)
    {
        var log    = _logs.GetOrAdd(clientKey, _ => new RequestLog());
        var result = log.TryRecord(policy.Limit, policy.Window);
        return Task.FromResult(result);
    }

    // ── Inner log — one instance per (clientKey) ──────────────────────────────

    private sealed class RequestLog
    {
        // SortedSet of ticks (100-nanosecond units) — O(log n) insert/remove.
        private readonly SortedSet<long> _timestamps = new();
        private readonly object            _lock = new();

        public RateLimitResult TryRecord(int limit, TimeSpan window)
        {
            lock (_lock)
            {
                var now              = DateTime.UtcNow;
                var windowStartTicks = (now - window).Ticks;

                // ── Evict expired timestamps ──────────────────────────────────
                while (_timestamps.Count > 0 && _timestamps.Min < windowStartTicks)
                    _timestamps.Remove(_timestamps.Min);

                var currentCount = _timestamps.Count;

                if (currentCount < limit)
                {
                    // Guarantee uniqueness — ticks can repeat at nanosecond resolution.
                    var ticks = now.Ticks;
                    while (_timestamps.Contains(ticks)) ticks++;
                    _timestamps.Add(ticks);

                    var remaining = limit - currentCount - 1;
                    return RateLimitResult.Allow(Math.Max(0, remaining));
                }

                // ── Compute precise retry-after ───────────────────────────────
                var oldestTime  = new DateTime(_timestamps.Min, DateTimeKind.Utc);
                var retryAfter  = (oldestTime + window) - now;
                return RateLimitResult.Deny(0,
                    retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero,
                    "Sliding window log limit reached");
            }
        }
    }
}
