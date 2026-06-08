using Ocelot.DependencyInjection;
using Ocelot.Middleware;
using RateLimiting.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

// ── Services ─────────────────────────────────────────────────────────────────

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title       = "Rate Limiting API",
        Version     = "v1",
        Description = "Demonstrates Token Bucket, Leaky Bucket, Fixed Window, " +
                      "Sliding Window Log, and Sliding Window Counter rate limiting " +
                      "with per-customer policies."
    });
    c.IncludeXmlComments(Path.Combine(
        AppContext.BaseDirectory, "RateLimiting.Api.xml"), includeControllerXmlComments: true);
});

builder.Configuration.AddJsonFile("ocelot.json", optional: false, reloadOnChange: true);

builder.Services.AddOcelot(builder.Configuration);
// ── Rate limiting (all five algorithms + customer policies) ──────────────────
builder.Services.AddRateLimiting();

// ── Optional: Redis for distributed rate limiting ────────────────────────────
// Uncomment when deploying multiple instances behind a load balancer.
//
// builder.Services.AddStackExchangeRedisCache(opts =>
//     opts.Configuration = builder.Configuration.GetConnectionString("Redis"));

// ── Build ─────────────────────────────────────────────────────────────────────

var app = builder.Build();

// ── Middleware pipeline ───────────────────────────────────────────────────────

//if (app.Environment.IsDevelopment())
//{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Rate Limiting API v1");
        c.RoutePrefix = string.Empty; // Swagger at root: https://localhost:5001/
    });
//}

app.UseHttpsRedirection();

// Rate limiting MUST come before authentication — cheap 429s before any auth work.
app.UseRateLimiting();
await app.UseOcelot();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
