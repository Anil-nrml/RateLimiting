using System.Net;
using System.Text.Json;
using RateLimiting.Api.Configuration;
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
    private readonly ICustomerPolicyStore _customerStore;
    private readonly OperationPolicyStore _operationStore;
    private readonly ILogger<RateLimitMiddleware> _logger;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RateLimitMiddleware(
        RequestDelegate           next,
        IRateLimiterFactory       factory,
        ICustomerPolicyStore customerStore,
        OperationPolicyStore operationStore,
        ILogger<RateLimitMiddleware> logger)
    {
        _next        = next;
        _factory     = factory;
        _customerStore = customerStore;
        _operationStore = operationStore;
        _logger      = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var customerId = ResolveCustomerId(context);
        var serviceId = ResolveServiceId(context);
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;

        // ── Layer 1: Customer-level check ────────────────────────────────
        var customerConfig = _customerStore.GetConfig(customerId);
        var customerFail = await EvaluatePoliciesAsync(customerId, customerConfig.Policies);

        if (customerFail is not null)
        {
            SetHeaders(context, customerFail, customerConfig);
            _logger.LogWarning(
                "Customer rate limit exceeded — customer={CustomerId} path={Path}",
                customerId, path);
            await WriteResponseAsync(context, customerFail, customerId, "customer");
            return;
        }

        SetHeaders(context, customerFail, customerConfig);

        // ── Layer 2: Service-level check ─────────────────────────────────
        if (!string.IsNullOrEmpty(serviceId))
        {
            var serviceConfig = _customerStore.GetConfig(serviceId);
            var serviceFail = await EvaluatePoliciesAsync(serviceId, serviceConfig.Policies);

            if (serviceFail is not null)
            {
                _logger.LogWarning(
                    "Service rate limit exceeded — service={ServiceId} customer={CustomerId} path={Path}",
                    serviceId, customerId, path);
                await WriteResponseAsync(context, serviceFail, customerId, serviceId);
                return;
            }
        }

        // ── Layer 3: Operation-level check ───────────────────────────────
        var opPolicy = _operationStore.GetPolicy(method, path);
        if (opPolicy is not null)
        {
            var opKey = $"{customerId}:{method}:{path}";
            var opLimiter = _factory.GetLimiter(opPolicy.Algorithm);
            var opResult = await opLimiter.IsAllowedAsync(opKey, opPolicy);

            if (!opResult.IsAllowed)
            {
                _logger.LogWarning(
                    "Operation rate limit exceeded — operation={Method}:{Path} customer={CustomerId}",
                    method, path, customerId);
                await WriteResponseAsync(context, opResult, customerId, $"{method}:{path}");
                return;
            }
        }

        await _next(context);
    }

    // ── Policy evaluation helper ─────────────────────────────────────────────

    private async Task<RateLimitResult?> EvaluatePoliciesAsync(
        string id, IEnumerable<RateLimitPolicy> policies)
    {
        foreach (var policy in policies)
        {
            var limiter = _factory.GetLimiter(policy.Algorithm);
            var result = await limiter.IsAllowedAsync($"{id}:{policy.Name}", policy);
            if (!result.IsAllowed)
                return result;
        }
        return null;
    }

    // ── Customer ID resolution ───────────────────────────────────────────────

    private static string ResolveCustomerId(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("X-Customer-ID", out var fromHeader)
            && !string.IsNullOrWhiteSpace(fromHeader))
            return fromHeader.ToString().ToLowerInvariant();

        var jwtClaim = ctx.User?.FindFirst("sub")?.Value
                    ?? ctx.User?.FindFirst("client_id")?.Value;
        if (!string.IsNullOrWhiteSpace(jwtClaim))
            return jwtClaim.ToLowerInvariant();

        if (ctx.Request.Headers.TryGetValue("X-Api-Key", out var apiKey)
            && !string.IsNullOrWhiteSpace(apiKey))
            return $"apikey:{apiKey}".ToLowerInvariant();

        return ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
    }

    // ── Service ID from Ocelot AddHeadersToRequest ───────────────────────────

    private static string? ResolveServiceId(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("X-Service-ID", out var svcId)
            && !string.IsNullOrWhiteSpace(svcId))
            return svcId.ToString().ToLowerInvariant();
        return null;
    }

    // ── Response headers ─────────────────────────────────────────────────────

    private static void SetHeaders(
        HttpContext ctx, RateLimitResult? result, CustomerRateLimitConfig config)
    {
        var policy = config.PrimaryPolicy;
        if (policy is null) return;

        ctx.Response.Headers["X-RateLimit-Limit"] = policy.Limit.ToString();
        ctx.Response.Headers["X-RateLimit-Algorithm"] = policy.Algorithm.ToString();
        ctx.Response.Headers["X-RateLimit-Reset"] =
            DateTimeOffset.UtcNow.Add(policy.Window).ToUnixTimeSeconds().ToString();

        if (result is not null)
            ctx.Response.Headers["X-RateLimit-Remaining"] = result.RemainingRequests.ToString();

        if (result is { IsAllowed: false } && result.RetryAfter > TimeSpan.Zero)
            ctx.Response.Headers["Retry-After"] =
                ((int)Math.Ceiling(result.RetryAfter.TotalSeconds)).ToString();
    }

    // ── 429 response ─────────────────────────────────────────────────────────

    private static async Task WriteResponseAsync(
        HttpContext ctx, RateLimitResult result, string customerId, string scope)
    {
        ctx.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
        ctx.Response.ContentType = "application/json";

        var body = JsonSerializer.Serialize(new
        {
            error = "rate_limit_exceeded",
            message = $"Rate limit exceeded for scope '{scope}'. Check Retry-After header.",
            customerId,
            scope,
            retryAfterSeconds = (int)Math.Ceiling(result.RetryAfter.TotalSeconds),
            retryAfter = result.RetryAfter > TimeSpan.Zero
                                    ? DateTimeOffset.UtcNow.Add(result.RetryAfter).ToString("O")
                                    : null
        }, _json);

        await ctx.Response.WriteAsync(body);
    }
}
