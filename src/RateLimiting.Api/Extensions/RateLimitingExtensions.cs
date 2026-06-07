using RateLimiting.Api.Configuration;
using RateLimiting.Api.Middleware;
using RateLimiting.Core.Algorithms;
using RateLimiting.Core.Interfaces;

namespace RateLimiting.Api.Extensions;

/// <summary>
/// Extension methods for registering rate-limiting services and middleware.
/// Keeps <c>Program.cs</c> clean — all wiring in one place.
/// </summary>
public static class RateLimitingExtensions
{
    /// <summary>
    /// Registers all rate-limiting services into the DI container.
    /// Call this in <c>Program.cs</c> before <c>builder.Build()</c>.
    /// </summary>
    public static IServiceCollection AddRateLimiting(this IServiceCollection services)
    {
        // ── Algorithm implementations (singleton — state must survive requests) ──
        services.AddSingleton<TokenBucketRateLimiter>();
        services.AddSingleton<LeakyBucketRateLimiter>();
        services.AddSingleton<FixedWindowCounterRateLimiter>();
        services.AddSingleton<SlidingWindowLogRateLimiter>();
        services.AddSingleton<SlidingWindowCounterRateLimiter>();

        // ── Factory resolves algorithm enum → implementation ──────────────────
        services.AddSingleton<IRateLimiterFactory, RateLimiterFactory>();

        // ── Customer policy store ─────────────────────────────────────────────
        // Swap InMemoryCustomerPolicyStore for a DB/Redis-backed implementation
        // in production without changing anything else in the pipeline.
        services.AddSingleton<ICustomerPolicyStore, InMemoryCustomerPolicyStore>();

        return services;
    }

    /// <summary>
    /// Adds the <see cref="RateLimitMiddleware"/> to the request pipeline.
    /// Place this call BEFORE <c>UseAuthentication</c> and <c>UseAuthorization</c>
    /// so that 429 responses are returned cheaply without running auth logic.
    /// </summary>
    public static IApplicationBuilder UseRateLimiting(this IApplicationBuilder app) =>
        app.UseMiddleware<RateLimitMiddleware>();
}
