using RateLimiting.Core.Models;

namespace RateLimiting.Api.Configuration;

/// <summary>
/// Maps (HTTP method + path prefix) → a fine-grained rate-limit policy.
///
/// This is the third and most granular layer:
///   Layer 1 — Customer   (who is calling)
///   Layer 2 — Service    (which microservice)
///   Layer 3 — Operation  (which specific endpoint + verb)  ← this class
///
/// Example: POST /payments/charge has a much tighter limit (10/min) than
///          GET  /payments/balance (60/min) even though both hit the Payment service.
/// </summary>
public sealed class OperationPolicyStore
{
    // Key format: "METHOD:path-prefix"  (method uppercase, path lowercase)
    private readonly IReadOnlyDictionary<string, RateLimitPolicy> _ops;

    public OperationPolicyStore()
    {
        _ops = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Order Service operations ──────────────────────────────────

            // Writing orders is rate-limited tighter than reading them
            ["POST:/employees"] = new RateLimitPolicy(
                "employee-create", Limit: 30, Window: TimeSpan.FromMinutes(1),
                Algorithm: RateLimitAlgorithm.TokenBucket),

            ["GET:/employees"] = new RateLimitPolicy(
                "employee-read", Limit: 300, Window: TimeSpan.FromMinutes(1),
                Algorithm: RateLimitAlgorithm.SlidingWindowCounter),

            ["PUT:/employees"] = new RateLimitPolicy(
                "employee-update", Limit: 50, Window: TimeSpan.FromMinutes(1),
                Algorithm: RateLimitAlgorithm.SlidingWindowCounter),

            // ── Payment Service operations ────────────────────────────────

            // Charge is the most sensitive: tight LeakyBucket (smooth, no bursts)
            ["POST:/product/charge"] = new RateLimitPolicy(
                "product-charge", Limit: 10, Window: TimeSpan.FromMinutes(1),
                Algorithm: RateLimitAlgorithm.LeakyBucket),

            // Refunds slightly more generous than charges
            ["POST:/products/refund"] = new RateLimitPolicy(
                "product-refund", Limit: 20, Window: TimeSpan.FromMinutes(1),
                Algorithm: RateLimitAlgorithm.LeakyBucket),

            // Balance/status reads are read-only and cheap
            ["GET:/products"] = new RateLimitPolicy(
                "payment-read", Limit: 60, Window: TimeSpan.FromMinutes(1),
                Algorithm: RateLimitAlgorithm.SlidingWindowCounter),

            //// ── Shipping Service operations ───────────────────────────────

            //// Label generation triggers external carrier API calls — expensive
            //["POST:/shipping/labels"] = new RateLimitPolicy(
            //    "shipping-label", Limit: 20, Window: TimeSpan.FromMinutes(1),
            //    Algorithm: RateLimitAlgorithm.LeakyBucket),

            //// Tracking lookups are read-only, higher limit
            //["GET:/shipping/track"] = new RateLimitPolicy(
            //    "shipping-track", Limit: 120, Window: TimeSpan.FromMinutes(1),
            //    Algorithm: RateLimitAlgorithm.SlidingWindowCounter),

            //// Rate estimation calls upstream carrier pricing APIs
            //["POST:/shipping/rates"] = new RateLimitPolicy(
            //    "shipping-rates", Limit: 30, Window: TimeSpan.FromMinutes(1),
            //    Algorithm: RateLimitAlgorithm.TokenBucket),
        };
    }

    /// <summary>
    /// Returns the operation-level policy for the given HTTP method and path,
    /// or null if no specific operation policy is configured (service-level applies).
    /// Uses longest-prefix matching.
    /// </summary>
    public RateLimitPolicy? GetPolicy(string method, string path)
    {
        // Try exact method + path prefix match, preferring longer (more specific) prefixes
        var match = _ops.Keys
            .Where(k =>
            {
                var parts = k.Split(':', 2);
                return parts.Length == 2
                    && parts[0].Equals(method, StringComparison.OrdinalIgnoreCase)
                    && path.StartsWith(parts[1], StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(k => k.Split(':', 2)[1].Length) // longest prefix wins
            .FirstOrDefault();

        return match is not null ? _ops[match] : null;
    }
}
