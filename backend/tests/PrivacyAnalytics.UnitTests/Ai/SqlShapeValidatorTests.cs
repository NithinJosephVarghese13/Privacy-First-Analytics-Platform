using PrivacyAnalytics.Infrastructure.Ai;
using Xunit;

namespace PrivacyAnalytics.UnitTests.Ai;

public class SqlShapeValidatorTests
{
    private readonly SqlShapeValidator _validator = new();

    [Theory]
    [InlineData("SELECT * FROM reporting_analytics_events")]
    [InlineData("SELECT path, COUNT(*) FROM reporting_top_pages GROUP BY path;")]
    [InlineData("WITH daily AS (SELECT event_date, SUM(total_events) FROM reporting_daily_pageviews GROUP BY event_date) SELECT * FROM daily;")]
    [InlineData("  SELECT path FROM reporting_analytics_events ORDER BY timestamp DESC LIMIT 5  ; ")]
    public void ValidQueries_PassValidation(string sql)
    {
        var (isValid, errorMessage) = _validator.Validate(sql);

        Assert.True(isValid, $"Expected valid SQL but got error: {errorMessage}");
        Assert.Null(errorMessage);
    }

    [Theory]
    [InlineData("SELECT * FROM reporting_analytics_events; DROP TABLE analytics_events;")]
    [InlineData("SELECT * FROM reporting_analytics_events; SELECT * FROM reporting_top_pages")]
    [InlineData("SELECT * FROM reporting_analytics_events; -- comment")]
    public void InternalSemicolons_AreRejected(string sql)
    {
        var (isValid, errorMessage) = _validator.Validate(sql);

        Assert.False(isValid);
        Assert.Contains("Semicolons are only permitted at the very end", errorMessage);
    }

    [Theory]
    [InlineData("SELECT * FROM reporting_analytics_events WHERE path IN (SELECT path FROM pg_user)")]
    [InlineData("SELECT * FROM reporting_analytics_events UNION SELECT * FROM pg_tables")]
    public void ForbiddenKeywords_AreRejected(string sql)
    {
        var (isValid, errorMessage) = _validator.Validate(sql);

        Assert.False(isValid);
        Assert.Contains("forbidden keyword or construct", errorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("INSERT INTO analytics_events VALUES ('1', '2')")]
    [InlineData("UPDATE analytics_events SET path = '/hacked'")]
    [InlineData("DELETE FROM analytics_events")]
    [InlineData("DROP TABLE reporting_analytics_events")]
    [InlineData("ALTER TABLE analytics_events DROP COLUMN path")]
    [InlineData("GRANT ALL ON analytics_events TO public")]
    [InlineData("COPY analytics_events TO '/tmp/data.csv'")]
    [InlineData("SHOW TABLES")]
    [InlineData("EXEC sp_help")]
    public void NonSelectQueries_AreRejected(string sql)
    {
        var (isValid, errorMessage) = _validator.Validate(sql);

        Assert.False(isValid);
        Assert.NotNull(errorMessage);
    }
}
