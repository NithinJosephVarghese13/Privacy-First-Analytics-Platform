using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PrivacyAnalytics.Infrastructure.Data;

public class AnalyticsDbContextFactory : IDesignTimeDbContextFactory<AnalyticsDbContext>
{
    public AnalyticsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ANALYTICS_DB_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=analytics;Username=analytics;Password=analytics_dev";

        var optionsBuilder = new DbContextOptionsBuilder<AnalyticsDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new AnalyticsDbContext(optionsBuilder.Options);
    }
}
