using System.Net;
using System.Text.Json;
using RateLimiting.Core.Interfaces;
using RateLimiting.Core.Models;

namespace RateLimiting.Api.Middleware;

/// <summary>
/// Rate-Limiting Middleware
/// ─────────────────────────
/// Pipeline position: registered BEFORE authentication so 429 responses are
/// returned cheaply, without executing token validation.
///
/// Per-request flow:
///   1. Resolve the caller's customer ID (header → JWT → API key → IP).
///   2. Load the customer's policy set from <see cref="ICustomerPolicyStore"/>.
///   3. Evaluate each policy in order — ALL must pass (AND semantics).
///   4. Write RFC-compliant rate-limit headers on every response.
///   5. Return HTTP 429 with a JSON body if any policy fails.
/// </summary>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate      _next;
    private readonly IRateLimiterFactory  _factory;
    private readonly ICustomerPolicyStore _policyStore;
    private readonly ILogger<RateLimitMiddleware> _logger;

    public RateLimitMiddleware(
        RequestDelegate           next,
        IRateLimiterFactory       factory,
        ICustomerPolicyStore      policyStore,
        ILogger<RateLimitMiddleware> logger)
    {
        _next        = next;
        _factory     = factory;
        _policyStore = policyStore;
        _logger      = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var customerId = ResolveCustomerId(context);
        var config     = _policyStore.GetConfig(customerId);

        RateLimitResult? failedResult = null;
        RateLimitResult? lastResult   = null;

        foreach (var policy in config.Policies)
        {
            // Compound key keeps each policy's counter independent.
            var limiterKey = $"{customerId}:{policy.Name}";
            var limiter    = _factory.GetLimiter(policy.Algorithm);
            var result     = await limiter.IsAllowedAsync(limiterKey, policy);

            lastResult = result;

            if (!result.IsAllowed)
            {
                failedResult = result;
                _logger.LogWarning(
                    "Rate limit exceeded — customer={CustomerId} policy={PolicyName} " +
                    "limit={Limit}/{Window} algorithm={Algorithm}",
                    customerId, policy.Name, policy.Limit, policy.Window, policy.Algorithm);
                break; // short-circuit on first failure
            }
        }

        SetRateLimitHeaders(context, lastResult, config);

        if (failedResult is not null)
        {
            await WriteRateLimitResponse(context, failedResult, customerId);
            return;
        }

        await _next(context);
    }

    // ── Customer ID resolution ────────────────────────────────────────────────
    // Precedence (highest → lowest):
    //   1. X-Customer-ID header  (injected by APIM after subscription resolution)
    //   2. JWT sub / client_id claim
    //   3. X-Api-Key header      (look up via your own key store in production)
    //   4. Remote IP address     (last resort)

    private static string ResolveCustomerId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Customer-ID", out var fromHeader)
            && !string.IsNullOrWhiteSpace(fromHeader))
            return fromHeader.ToString().ToLowerInvariant();

        var jwtClaim = context.User?.FindFirst("sub")?.Value
                    ?? context.User?.FindFirst("client_id")?.Value;
        if (!string.IsNullOrWhiteSpace(jwtClaim))
            return jwtClaim.ToLowerInvariant();

        if (context.Request.Headers.TryGetValue("X-Api-Key", out var apiKey)
            && !string.IsNullOrWhiteSpace(apiKey))
            return $"apikey:{apiKey}".ToLowerInvariant();

        return context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
    }

    // ── RFC 6585 / IETF Rate-Limit Headers draft ─────────────────────────────

    private static void SetRateLimitHeaders(
        HttpContext              context,
        RateLimitResult?         result,
        CustomerRateLimitConfig  config)
    {
        var policy = config.PrimaryPolicy;
        if (policy is null || result is null) return;

        var headers = context.Response.Headers;
        headers["X-RateLimit-Limit"]     = policy.Limit.ToString();
        headers["X-RateLimit-Remaining"] = result.RemainingRequests.ToString();
        headers["X-RateLimit-Algorithm"] = policy.Algorithm.ToString();
        headers["X-RateLimit-Reset"]     = DateTimeOffset.UtcNow.Add(policy.Window)
                                               .ToUnixTimeSeconds().ToString();

        if (!result.IsAllowed && result.RetryAfter > TimeSpan.Zero)
            headers["Retry-After"] = ((int)Math.Ceiling(result.RetryAfter.TotalSeconds)).ToString();
    }

    // ── 429 response body ─────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static async Task WriteRateLimitResponse(
        HttpContext      context,
        RateLimitResult  result,
        string           customerId)
    {
        context.Response.StatusCode  = (int)HttpStatusCode.TooManyRequests;
        context.Response.ContentType = "application/json";

        var body = JsonSerializer.Serialize(new
        {
            error            = "rate_limit_exceeded",
            message          = "Too many requests. Check the Retry-After header and slow down.",
            customerId,
            retryAfterSeconds = (int)Math.Ceiling(result.RetryAfter.TotalSeconds),
            retryAfter        = result.RetryAfter > TimeSpan.Zero
                                    ? DateTimeOffset.UtcNow.Add(result.RetryAfter).ToString("O")
                                    : null,
            documentationUrl  = "https://your-api.com/docs/rate-limits"
        }, _jsonOptions);

        await context.Response.WriteAsync(body);
    }
}
