using Microsoft.EntityFrameworkCore;
using PrivacyAnalytics.Infrastructure.Identity;
using PrivacyAnalytics.Worker;
var builder = Host.CreateApplicationBuilder(args);

// The worker consumes the ingestion queue and persists events, so it must source the SAME
// durable-HMAC key and daily-salt seed out-of-band (Docker secrets) as the API — never from a DB
// column or appsettings literal (FR-2.1).
builder.Services.AddIdentityHashing(builder.Configuration);

builder.Services.AddDbContext<PrivacyAnalytics.Infrastructure.Data.AnalyticsDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("AnalyticsDb");
    options.UseNpgsql(connectionString);
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
