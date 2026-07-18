using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PrivacyAnalytics.Api.Middleware;
using PrivacyAnalytics.Contracts;
using PrivacyAnalytics.Domain.Identity;
using PrivacyAnalytics.Infrastructure.Data;
using PrivacyAnalytics.Infrastructure.Identity;
using PrivacyAnalytics.Infrastructure.Messaging;
using PrivacyAnalytics.Domain.Messaging;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using PrivacyAnalytics.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Disable default inbound claim mapping so custom claims like "tenant_id" remain intact
JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var authority = builder.Configuration["Authentication:Authority"] ?? "http://localhost:8080/realms/analytics-platform";
        options.Authority = authority;
        options.RequireHttpsMetadata = false; // Internal Docker / local dev network
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = false, // Not validating audience for internal tokens in this MVP
            ValidateIssuer = true,
            ValidIssuer = authority
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentTenant, CurrentTenant>();

builder.Services.AddDbContext<AnalyticsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("AnalyticsDb")));

builder.Services.AddScoped<IDapperQueryHelper, DapperQueryHelper>();
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(PrivacyAnalytics.Infrastructure.Analytics.Queries.GetPageviewsOverTimeQueryHandler).Assembly));

// Two-tier identity hashing (FR-2.1): daily-salt SHA-256 for anonymous traffic and a
// tenant-scoped HMAC for authenticated, opted-in traffic. The HMAC signing key is read from a
// Docker secret file — never appsettings, never an env var holding the literal value, never a DB
// column (see docker-compose.yml for the secret mount).
builder.Services.AddIdentityHashing(builder.Configuration);
builder.Services.AddMessaging();

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

app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/api/v1/track", async (
        TrackRequest payload,
        HttpContext httpContext,
        IIdentityHashService identityHashService,
        IMessagePublisher publisher,
        IConfiguration configuration,
        ILogger<Program> logger) =>
    {
        // MVP: compute the two-tier pseudonyms and return 202 immediately. No RabbitMQ publish yet
        // (that's Module 2). The payload at this point has had its query string stripped by
        // UrlScrubbingMiddleware, so logging it does not leak query-string PII.

        // Identity context. In production these come from the VERIFIED auth token (Keycloak JWT),
        // never from client-controllable input. For v1 they are injected as request headers by the
        // upstream identity proxy — the same trust boundary as X-Tenant-Origin — so a spoofing actor
        // is one that already controls the beacon client, which is the documented threat model.
        // We now treat the X-Tenant-Id header as the PublicWriteKey for the client ingestion.
        var tenantIdHeader = httpContext.Request.Headers["X-Tenant-Id"].ToString();
        var userIdHeader = httpContext.Request.Headers["X-User-Id"].ToString();
        var optedIn = httpContext.Request.Headers["X-User-Opted-In"].ToString()
            .Equals("true", StringComparison.OrdinalIgnoreCase);

        if (!Guid.TryParse(tenantIdHeader, out var writeKey))
        {
            return Results.BadRequest(new { Error = "Invalid X-Tenant-Id header format. Must be a valid GUID." });
        }

        // Validate the PublicWriteKey and get the internal OrganizationId using Dapper
        await using var connection = new Npgsql.NpgsqlConnection(configuration.GetConnectionString("AnalyticsDb"));
        await connection.OpenAsync();
        
        var organizationId = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<Guid?>(
            connection,
            "SELECT org_id FROM organizations WHERE public_write_key = @WriteKey",
            new { WriteKey = writeKey });

        if (organizationId == null)
        {
            return Results.BadRequest(new { Error = "Invalid X-Tenant-Id. Unknown tenant." });
        }

        var isAuthenticated = !string.IsNullOrWhiteSpace(userIdHeader);
        var clientIp = ResolveClientIp(httpContext.Request);
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();

        var utcDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        var identityInput = new IdentityHashInput(
            organizationId.Value,
            isAuthenticated,
            optedIn,
            UserId: isAuthenticated ? userIdHeader : null,
            ClientIp: clientIp,
            UserAgent: string.IsNullOrWhiteSpace(userAgent) ? null : userAgent);

        var hashes = identityHashService.Compute(identityInput, utcDate);

        logger.LogInformation(
            "AnalyticsEventReceived: Url='{Url}', EventType='{EventType}', IsAuthenticated={IsAuth}, " +
            "OptedIn={OptedIn}, AnonymousDailyHash='{AnonymousHash}' (forced null when authenticated), " +
            "DurableHash='{DurableHash}'",
            payload.Url ?? "<null>",
            payload.EventType ?? "<null>",
            isAuthenticated,
            optedIn,
            hashes.AnonymousDailyHash ?? "<null>",
            hashes.DurableHash ?? "<null>");

        var eventMessage = new AnalyticsEventReceived
        {
            EventId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            OrganizationId = organizationId.Value,
            AnonymousDailyHash = hashes.AnonymousDailyHash,
            DurableHash = hashes.DurableHash,
            IsAuthenticated = isAuthenticated,
            EventType = payload.EventType ?? string.Empty,
            Path = payload.Url ?? string.Empty
        };

        await publisher.PublishAsync(eventMessage);

        return Results.Accepted();
    })
    .RequireRateLimiting("TenantOrigin");

// Extracts the client IP preferring the first hop of X-Forwarded-For (set by the trusted edge
// proxy) and falling back to the direct remote IP. Only the first forwarded hop is trusted; a
// production deployment should pin this to a known proxy and validate the header chain.
static string? ResolveClientIp(HttpRequest request)
{
    var forwarded = request.Headers["X-Forwarded-For"].ToString();
    if (!string.IsNullOrWhiteSpace(forwarded))
    {
        var first = forwarded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first;
        }
    }
    return request.HttpContext.Connection.RemoteIpAddress?.ToString();
}

app.Run();

public partial class Program {}
