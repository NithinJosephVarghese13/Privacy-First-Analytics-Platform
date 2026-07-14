using Microsoft.EntityFrameworkCore;
using PrivacyAnalytics.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AnalyticsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("AnalyticsDb")));

var app = builder.Build();

app.Run();
