# Rate Limiting — .NET 8 API

Five rate-limiting algorithms with per-customer policies, Azure APIM integration, and xUnit tests.

---

## Project Structure

```
RateLimiting.sln
├── src/
│   ├── RateLimiting.Core/               # Algorithm library (no ASP.NET dependency)
│   │   ├── Algorithms/
│   │   │   ├── TokenBucketRateLimiter.cs
│   │   │   ├── LeakyBucketRateLimiter.cs
│   │   │   ├── FixedWindowCounterRateLimiter.cs
│   │   │   ├── SlidingWindowLogRateLimiter.cs
│   │   │   ├── SlidingWindowCounterRateLimiter.cs
│   │   │   ├── RedisRateLimiter.cs
│   │   │   └── RateLimiterFactory.cs
│   │   ├── Interfaces/
│   │   │   └── IRateLimiter.cs          # IRateLimiter, IRateLimiterFactory, ICustomerPolicyStore
│   │   └── Models/
│   │       └── RateLimitModels.cs       # RateLimitResult, RateLimitPolicy, enums
│   │
│   └── RateLimiting.Api/                # ASP.NET Core 8 Web API
│       ├── Controllers/
│       │   └── DemoController.cs        # /api/demo/ping, /api/demo/policy/{id}
│       ├── Middleware/
│       │   └── RateLimitMiddleware.cs   # Pipeline middleware (429 enforcement)
│       ├── Configuration/
│       │   └── InMemoryCustomerPolicyStore.cs  # Customer A / B / default policies
│       ├── Extensions/
│       │   └── RateLimitingExtensions.cs       # AddRateLimiting() / UseRateLimiting()
│       ├── Program.cs
│       ├── appsettings.json
│       └── appsettings.Development.json
│
├── tests/
│   └── RateLimiting.Tests/
│       ├── Algorithms/
│       │   └── AlgorithmTests.cs        # Unit tests for all 5 algorithms
│       └── Integration/
│           └── IntegrationTests.cs      # Full pipeline integration tests
│
└── docs/
    └── apim-policy.xml                  # Azure APIM inbound policy + Terraform snippets
```

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- (Optional) Redis — only needed for distributed multi-instance mode

---

## Run

```bash
# 1. Clone / extract the solution
cd RateLimitingSolution

# 2. Restore packages
dotnet restore

# 3. Run the API
cd src/RateLimiting.Api
dotnet run

# Swagger UI opens at: https://localhost:5001
```

---

## Test

```bash
# From solution root
dotnet test

# Verbose output
dotnet test --logger "console;verbosity=detailed"
```

---

## Try It — curl Examples

```bash
BASE=https://localhost:5001

# ── Customer A (Enterprise — 1000 req/sec) ─────────────────────────────────
curl -k -H "X-Customer-ID: customer-a" $BASE/api/demo/ping

# ── Customer B (Standard — 100 req/sec) ────────────────────────────────────
curl -k -H "X-Customer-ID: customer-b" $BASE/api/demo/ping

# ── View Customer B's policies ──────────────────────────────────────────────
curl -k $BASE/api/demo/policy/customer-b | jq

# ── Burst test — send 200 rapid requests as Customer B ─────────────────────
# You'll see 200 OKs then 429s with Retry-After
for i in $(seq 1 200); do
  curl -sk -o /dev/null -w "%{http_code}\n" \
    -H "X-Customer-ID: customer-b" $BASE/api/demo/ping
done

# ── Check rate-limit response headers ──────────────────────────────────────
curl -k -I -H "X-Customer-ID: customer-a" $BASE/api/demo/ping
# X-RateLimit-Limit: 1000
# X-RateLimit-Remaining: 999
# X-RateLimit-Reset: 1748687000
# X-RateLimit-Algorithm: TokenBucket
```

---

## Algorithms Summary

| Algorithm           | Burst | Memory | Accuracy | Best For                          |
|---------------------|-------|--------|----------|-----------------------------------|
| Token Bucket        | ✓     | O(1)   | High     | General APIs, public endpoints    |
| Leaky Bucket        | ✗     | O(1)   | High     | Payments, DB writes, smooth output|
| Fixed Window        | ✗*    | O(1)   | Medium   | Internal APIs, admin endpoints    |
| Sliding Window Log  | ✓     | O(n)   | Exact    | Billing APIs, low-medium traffic  |
| Sliding Window Counter | ✓  | O(1)   | ~99%     | High-traffic production default   |

\* Fixed Window allows 2× the limit at window boundaries.

---

## Customer Policies (configured in `InMemoryCustomerPolicyStore.cs`)

| Customer   | Per-Second | Burst | Per-Hour      | Algorithm (s)              |
|------------|-----------|-------|---------------|----------------------------|
| customer-a | 1 000     | 1 500 | 2 000 000     | Token Bucket + Sliding Counter |
| customer-b | 100       | 150   | 200 000       | Token Bucket + Sliding Counter |
| default    | —         | —     | 60 req/min    | Fixed Window Counter       |

**AND semantics** — all policies must pass. First failure returns 429.

---

## Add a New Customer

Edit `InMemoryCustomerPolicyStore.cs`:

```csharp
["customer-c"] = new CustomerRateLimitConfig(
    CustomerId: "customer-c",
    Policies:
    [
        new RateLimitPolicy(
            Name:      "per-second",
            Limit:     500,
            Window:    TimeSpan.FromSeconds(1),
            BurstLimit: 750,
            Algorithm: RateLimitAlgorithm.TokenBucket),
    ]),
```

---

## Distributed Mode (Redis)

For multiple API instances behind a load balancer:

1. Uncomment Redis in `Program.cs`:
   ```csharp
   builder.Services.AddStackExchangeRedisCache(opts =>
       opts.Configuration = builder.Configuration.GetConnectionString("Redis"));
   ```

2. Set `ConnectionStrings:Redis` in `appsettings.json`:
   ```json
   "ConnectionStrings": {
     "Redis": "your-redis.redis.cache.windows.net:6380,password=xxx,ssl=true"
   }
   ```

3. Register `RedisRateLimiter` instead of the in-memory algorithms in `RateLimitingExtensions.cs`.

---

## Azure APIM

See `docs/apim-policy.xml` for the complete inbound policy including:
- Per-subscription `rate-limit-by-key` for Customer A and B
- Hourly quota guard
- Custom 429 JSON response body
- Terraform deployment snippets
