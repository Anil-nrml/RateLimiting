using System.Collections.Concurrent;
using RateLimiting.Core.Interfaces;
using RateLimiting.Core.Models;

namespace RateLimiting.Core.Algorithms;

/// <summary>
/// Token Bucket Rate Limiter
/// ─────────────────────────
/// Behaviour:
///   • Tokens accumulate at <c>Limit / Window</c> tokens per second, up to <c>EffectiveBurst</c>.
///   • Each request consumes one token.
///   • If the bucket is empty the request is denied until the next token arrives.
///
/// Characteristics:
///   ✓ Allows controlled bursting
///   ✓ O(1) memory per client
///   ✓ Thread-safe via per-bucket lock
///
/// Best for: Public APIs, SDKs, endpoints that need burst tolerance.
/// </summary>
public sealed class TokenBucketRateLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();

    public Task<RateLimitResult> IsAllowedAsync(string clientKey, RateLimitPolicy policy)
    {
        var bucket = _buckets.GetOrAdd(
            clientKey,
            _ => new TokenBucket(policy.EffectiveBurst, policy.Limit, policy.Window));

        return Task.FromResult(bucket.TryConsume());
    }

    // ── Inner bucket — one instance per (clientKey) ───────────────────────────

    private sealed class TokenBucket
    {
        private double   _tokens;
        private DateTime _lastRefill;
        private readonly double _capacity;    // max tokens (burst ceiling)
        private readonly double _refillRate;  // tokens added per second
        private readonly object   _lock = new();

        public TokenBucket(int capacity, int limit, TimeSpan window)
        {
            _capacity   = capacity;
            _refillRate = limit / window.TotalSeconds;
            _tokens     = capacity;    // start full
            _lastRefill = DateTime.UtcNow;
        }

        public RateLimitResult TryConsume()
        {
            lock (_lock)
            {
                Refill();

                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return RateLimitResult.Allow((int)_tokens);
                }

                return RateLimitResult.Deny(0, RetryAfter(), "Token bucket empty");
            }
        }

        // Add tokens proportional to elapsed time since last refill.
        private void Refill()
        {
            var now     = DateTime.UtcNow;
            var elapsed = (now - _lastRefill).TotalSeconds;
            _tokens     = Math.Min(_capacity, _tokens + elapsed * _refillRate);
            _lastRefill = now;
        }

        // Time until exactly one token becomes available.
        private TimeSpan RetryAfter() =>
            _tokens >= 1
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((1 - _tokens) / _refillRate);
    }
}
