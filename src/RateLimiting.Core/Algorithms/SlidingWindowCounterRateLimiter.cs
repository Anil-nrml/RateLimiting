using System.Collections.Concurrent;
using RateLimiting.Core.Interfaces;
using RateLimiting.Core.Models;

namespace RateLimiting.Core.Algorithms;

/// <summary>
/// Sliding Window Counter Rate Limiter
/// ─────────────────────────────────────
/// Behaviour:
///   • Maintains counters for the current and previous fixed windows.
///   • Estimates the request count within the true rolling window using:
///
///       estimate = prev_count × (1 - window_progress) + current_count
///
///   • This weighted interpolation approximates the sliding log with O(1) memory.
///
/// Characteristics:
///   ✓ ~99 % accuracy compared to sliding log
///   ✓ O(1) memory per client — only 2 integers per key
///   ✓ Redis-friendly: maps naturally to two atomic INCR keys
///   ✓ Thread-safe via per-state lock
///   ✗ Small approximation error at window boundaries (typically &lt;1 %)
///
/// Best for: High-traffic production APIs, multi-instance deployments with Redis.
/// </summary>
public sealed class SlidingWindowCounterRateLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<string, SlidingWindowState> _states = new();

    public Task<RateLimitResult> IsAllowedAsync(string clientKey, RateLimitPolicy policy)
    {
        var state  = _states.GetOrAdd(clientKey, _ => new SlidingWindowState());
        var result = state.TryIncrement(policy.Limit, policy.Window);
        return Task.FromResult(result);
    }

    // ── Inner state — one instance per (clientKey) ────────────────────────────

    private sealed class SlidingWindowState
    {
        private long _currentWindowKey;
        private int  _currentCount;
        private int  _previousCount;
        private readonly object _lock = new();

        public RateLimitResult TryIncrement(int limit, TimeSpan window)
        {
            lock (_lock)
            {
                var now            = DateTime.UtcNow;
                var windowSeconds  = window.TotalSeconds;
                var epochSeconds   = (now - DateTime.UnixEpoch).TotalSeconds;
                var currentWinKey  = (long)(epochSeconds / windowSeconds);
                var windowProgress = (epochSeconds % windowSeconds) / windowSeconds; // 0.0 → 1.0

                // ── Rotate counters when the window advances ──────────────────
                if (currentWinKey != _currentWindowKey)
                {
                    _previousCount   = currentWinKey == _currentWindowKey + 1
                        ? _currentCount   // adjacent window — keep previous
                        : 0;              // gap of ≥2 windows — previous is irrelevant
                    _currentCount    = 0;
                    _currentWindowKey = currentWinKey;
                }

                // ── Weighted estimate of true sliding count ───────────────────
                // As the window progresses, the previous window's contribution fades.
                var estimate = _previousCount * (1.0 - windowProgress) + _currentCount;

                if (estimate < limit)
                {
                    _currentCount++;
                    var remaining = (int)Math.Max(0, limit - estimate - 1);
                    return RateLimitResult.Allow(remaining);
                }

                // Estimate time until one slot opens up in the sliding window.
                var secondsUntilSlot = windowSeconds * (1.0 - windowProgress) / Math.Max(1, limit);
                return RateLimitResult.Deny(0,
                    TimeSpan.FromSeconds(secondsUntilSlot),
                    "Sliding window counter limit reached");
            }
        }
    }
}
