using System.Collections.Concurrent;
using RateLimiting.Core.Interfaces;
using RateLimiting.Core.Models;

namespace RateLimiting.Core.Algorithms;

/// <summary>
/// Leaky Bucket Rate Limiter
/// ─────────────────────────
/// Behaviour:
///   • Requests fill a virtual queue (the "bucket").
///   • The queue drains at a fixed rate (<c>Limit / Window</c> per second).
///   • If the bucket is full, the incoming request is dropped immediately.
///
/// Characteristics:
///   ✓ Perfectly smooth output — backend always sees a constant request rate
///   ✓ O(1) memory per client
///   ✗ No burst allowance — spikes are dropped, not queued
///   ✗ Adds latency perception at burst time
///
/// Best for: Payment processors, database writes, downstream services that must
///           not receive traffic spikes.
/// </summary>
public sealed class LeakyBucketRateLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<string, LeakyBucket> _buckets = new();

    public Task<RateLimitResult> IsAllowedAsync(string clientKey, RateLimitPolicy policy)
    {
        var bucket = _buckets.GetOrAdd(
            clientKey,
            _ => new LeakyBucket(policy.EffectiveBurst, policy.Limit, policy.Window));

        return Task.FromResult(bucket.TryEnqueue());
    }

    // ── Inner bucket — one instance per (clientKey) ───────────────────────────

    private sealed class LeakyBucket
    {
        private double   _waterLevel;   // current number of queued requests
        private DateTime _lastLeak;
        private readonly double _capacity;   // max queue depth (burst limit)
        private readonly double _leakRate;   // requests drained per second
        private readonly object   _lock = new();

        public LeakyBucket(int capacity, int limit, TimeSpan window)
        {
            _capacity  = capacity;
            _leakRate  = limit / window.TotalSeconds;
            _waterLevel = 0;
            _lastLeak  = DateTime.UtcNow;
        }

        public RateLimitResult TryEnqueue()
        {
            lock (_lock)
            {
                Leak(); // drain processed requests first

                if (_waterLevel < _capacity)
                {
                    _waterLevel += 1;
                    var remaining = (int)(_capacity - _waterLevel);
                    return RateLimitResult.Allow(remaining);
                }

                // Bucket full — request dropped
                var retryAfter = TimeSpan.FromSeconds(1.0 / _leakRate);
                return RateLimitResult.Deny(0, retryAfter, "Leaky bucket full");
            }
        }

        // Reduce water level based on elapsed time (requests have "leaked out").
        private void Leak()
        {
            var now    = DateTime.UtcNow;
            var leaked = (now - _lastLeak).TotalSeconds * _leakRate;
            _waterLevel = Math.Max(0, _waterLevel - leaked);
            _lastLeak   = now;
        }
    }
}
