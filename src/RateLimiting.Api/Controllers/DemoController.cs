using Microsoft.AspNetCore.Mvc;
using RateLimiting.Core.Interfaces;
using RateLimiting.Core.Models;

namespace RateLimiting.Api.Controllers;

/// <summary>
/// Demo endpoints for testing rate limiting behaviour.
///
/// Test with curl:
///   # As Customer A (Enterprise — 1000/sec):
///   curl -H "X-Customer-ID: customer-a" https://localhost:5001/api/demo/ping
///
///   # As Customer B (Standard — 100/sec):
///   curl -H "X-Customer-ID: customer-b" https://localhost:5001/api/demo/ping
///
///   # Burst test (sends 200 requests rapidly):
///   for i in {1..200}; do curl -s -o /dev/null -w "%{http_code}\n" \
///     -H "X-Customer-ID: customer-b" https://localhost:5001/api/demo/ping; done
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class DemoController : ControllerBase
{
    private readonly ICustomerPolicyStore _policyStore;

    public DemoController(ICustomerPolicyStore policyStore)
    {
        _policyStore = policyStore;
    }

    /// <summary>
    /// Simple health-check endpoint.
    /// The rate-limiting middleware evaluates before this action runs.
    /// On success: 200. If limited: 429 (never reaches this action).
    /// </summary>
    [HttpGet("ping")]
    [ProducesResponseType(200)]
    [ProducesResponseType(429)]
    public IActionResult Ping()
    {
        var customerId = Request.Headers["X-Customer-ID"].FirstOrDefault() ?? "anonymous";
        return Ok(new
        {
            message    = "pong",
            customerId,
            timestamp  = DateTimeOffset.UtcNow,
            headers = new
            {
                rateLimitLimit     = Request.Headers["X-RateLimit-Limit"].FirstOrDefault(),
                rateLimitRemaining = Request.Headers["X-RateLimit-Remaining"].FirstOrDefault(),
                rateLimitReset     = Request.Headers["X-RateLimit-Reset"].FirstOrDefault(),
                algorithm          = Request.Headers["X-RateLimit-Algorithm"].FirstOrDefault(),
            }
        });
    }

    /// <summary>
    /// Returns the current rate-limit policy for a given customer.
    /// Useful for dashboards and debugging.
    /// </summary>
    [HttpGet("policy/{customerId}")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(404)]
    public IActionResult GetPolicy(string customerId)
    {
        var config = _policyStore.GetConfig(customerId);

        return Ok(new
        {
            customerId = config.CustomerId,
            policies   = config.Policies.Select(p => new
            {
                p.Name,
                p.Limit,
                window    = p.Window.ToString(),
                burstLimit = p.BurstLimit,
                algorithm  = p.Algorithm.ToString(),
            })
        });
    }

    /// <summary>
    /// Simulates a slow endpoint (100ms) to show that rate limiting
    /// acts before the endpoint executes.
    /// </summary>
    [HttpGet("slow")]
    [ProducesResponseType(200)]
    [ProducesResponseType(429)]
    public async Task<IActionResult> Slow(CancellationToken ct)
    {
        await Task.Delay(100, ct);
        return Ok(new { message = "Slow response", timestamp = DateTimeOffset.UtcNow });
    }
}
