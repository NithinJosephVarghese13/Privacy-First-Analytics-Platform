using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PrivacyAnalytics.Api.Middleware;
using PrivacyAnalytics.Contracts;
using PrivacyAnalytics.Infrastructure.Data;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AnalyticsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("AnalyticsDb")));

builder.Services.AddLogging();

// Rate limiting: per-tenant-origin token bucket, 100 req/sec per partition. The tenant origin is
// taken from the `X-Tenant-Origin` request header (chosen over subdomain extraction so the partition
// key is explicit, spoofable only by an actor that already controls the client — which is the
// threat model for a beacon endpoint — and independent of DNS/TLS configuration). A missing header
// collapses to a single "unknown" partition so unidentified traffic cannot trivially bypass the
// limiter by omitting the header; that shared bucket is a deliberate choke-point, not a per-tenant
// budget. Each named partition gets its own TokenBucketRateLimiter with a 100-token capacity that
// replenishes 100 tokens every second, i.e. a sustained 100 req/sec ceiling per tenant origin.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("TenantOrigin", context =>
    {
        var tenantOrigin = context.Request.Headers["X-Tenant-Origin"].ToString();
        if (string.IsNullOrWhiteSpace(tenantOrigin))
        {
            tenantOrigin = "unknown";
        }

        return RateLimitPartition.GetTokenBucketLimiter(
            tenantOrigin,
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 100,
                TokensPerPeriod = 100,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

var app = builder.Build();

// Order matters: URL scrubbing must run before anything else can observe the payload, so it is the
// first middleware. Rate limiting follows so that scrubbing is applied even to requests that end up
// throttled (cheap, in-memory) — though for a flood of 429s we pay the buffering cost. An
// alternative is to rate-limit first; we choose scrub-first so the privacy guarantee is
// unconditional regardless of throttling decisions.
app.UseMiddleware<UrlScrubbingMiddleware>();
app.UseRateLimiter();

app.MapPost("/api/v1/track", (TrackRequest payload, ILogger<Program> logger) =>
    {
        // MVP: log the (already-scrubbed) payload and return 202 immediately. No hashing, no
        // RabbitMQ publish yet — that's Module 2. The payload at this point has had its query
        // string stripped by UrlScrubbingMiddleware, so logging it does not leak query-string PII.
        logger.LogInformation(
            "AnalyticsEventReceived (unprocessed): Url='{Url}', ReferralSource='{ReferralSource}', EventType='{EventType}'",
            payload.Url ?? "<null>",
            payload.ReferralSource ?? "<null>",
            payload.EventType ?? "<null>");

        return Results.Accepted();
    })
    .RequireRateLimiting("TenantOrigin");

app.Run();
