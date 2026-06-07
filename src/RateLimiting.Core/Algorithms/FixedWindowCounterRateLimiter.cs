using System.Collections.Concurrent;
using RateLimiting.Core.Interfaces;
using RateLimiting.Core.Models;

namespace RateLimiting.Core.Algorithms;

/// <summary>
/// Fixed Window Counter Rate Limiter
/// ──────────────────────────────────
/// Behaviour:
///   • Time is divided into fixed-length windows (e.g. every 60 s).
///   • Each client gets an independent counter per window.
///   • Counter resets to zero at window boundary.
///
/// Characteristics:
///   ✓ Extremely simple — O(1) per request
///   ✓ Easy to reason about and explain to stakeholders
///   ✗ Boundary burst: clients can send 2× the limit straddling two windows
///       (e.g. 100 at the end of window N and 100 at the start of window N+1)
///
/// Best for: Internal admin APIs, dashboards, low-sensitivity endpoints.
/// </summary>
public sealed class FixedWindowCounterRateLimiter : IRateLimiter
{
    // Key = "clientKey:windowBucket" — one counter per client per window slot.
    private readonly ConcurrentDictionary<string, WindowCounter> _counters = new();

    public Task<RateLimitResult> IsAllowedAsync(string clientKey, RateLimitPolicy policy)
    {
        var windowKey    = ComputeWindowKey(policy.Window);
        var counterKey   = $"{clientKey}:{windowKey}";
        var windowExpiry = ComputeWindowExpiry(policy.Window);

        var counter = _counters.GetOrAdd(counterKey, _ => new WindowCounter());

        // Opportunistically remove stale counters to prevent unbounded growth.
        PurgeStaleCounters(policy.Window, windowKey);

        return Task.FromResult(counter.TryIncrement(policy.Limit, windowExpiry));
    }

    // ── Window helpers ────────────────────────────────────────────────────────

    /// <summary>Returns an integer bucket key for the current fixed window.</summary>
    private static long ComputeWindowKey(TimeSpan window)
    {
        var epochSeconds  = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        var windowSeconds = (long)window.TotalSeconds;
        return epochSeconds / windowSeconds;
    }

    /// <summary>Returns the UTC instant at which the current window expires.</summary>
    private static DateTime ComputeWindowExpiry(TimeSpan window)
    {
        var epochSeconds  = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        var windowSeconds = (long)window.TotalSeconds;
        var windowEnd     = (epochSeconds / windowSeconds + 1) * windowSeconds;
        return DateTime.UnixEpoch.AddSeconds(windowEnd);
    }

    private void PurgeStaleCounters(TimeSpan window, long currentWindowKey)
    {
        foreach (var key in _counters.Keys)
        {
            // Key format: "clientKey:windowBucket" — the last segment is the bucket number.
            var lastColon = key.LastIndexOf(':');
            if (lastColon < 0) continue;
            if (long.TryParse(key[(lastColon + 1)..], out var bucket) && bucket < currentWindowKey)
                _counters.TryRemove(key, out _);
        }
    }

    // ── Inner counter — one instance per (clientKey, window) ─────────────────

    private sealed class WindowCounter
    {
        private int      _count;
        private DateTime _windowExpiry;

        public RateLimitResult TryIncrement(int limit, DateTime windowExpiry)
        {
            var newCount = Interlocked.Increment(ref _count);
            _windowExpiry = windowExpiry;

            if (newCount <= limit)
            {
                var remaining = limit - newCount;
                return RateLimitResult.Allow(remaining);
            }

            var retryAfter = windowExpiry - DateTime.UtcNow;
            return RateLimitResult.Deny(0,
                retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero,
                "Fixed window limit reached");
        }
    }
}
