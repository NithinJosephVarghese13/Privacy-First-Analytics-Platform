using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PrivacyAnalytics.Infrastructure.Ai;
using Xunit;

namespace PrivacyAnalytics.UnitTests.Ai;

public class OpenRouterTextToSqlServiceTests
{
    private readonly OpenRouterTextToSqlService _service;

    public OpenRouterTextToSqlServiceTests()
    {
        var httpClient = new HttpClient();
        var shapeValidator = new SqlShapeValidator();
        var configuration = new ConfigurationBuilder().Build();
        var logger = NullLogger<OpenRouterTextToSqlService>.Instance;

        _service = new OpenRouterTextToSqlService(
            httpClient,
            shapeValidator,
            configuration,
            logger);
    }

    [Theory]
    [InlineData("Top 5 pages by unique visitors this week")]
    [InlineData("Daily pageviews for the past month")]
    [InlineData("Known vs estimated unique visitors count")]
    [InlineData("Show top pages by total events")]
    public async Task GenerateSqlAsync_WhenUseCacheIsTrue_ReturnsCachedResponseWithZeroNetworkCall(string prompt)
    {
        // Act
        var (sql, isCached) = await _service.GenerateSqlAsync(prompt, useCache: true, CancellationToken.None);

        // Assert
        Assert.True(isCached);
        Assert.False(string.IsNullOrWhiteSpace(sql));
        Assert.StartsWith("SELECT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateSqlAsync_WhenPromptMatchesCuratedDemoPrompt_ReturnsCachedResponse()
    {
        // Act
        var (sql, isCached) = await _service.GenerateSqlAsync("Show me the top 5 pages by unique visitors this week.", useCache: false);

        // Assert
        Assert.True(isCached);
        Assert.Contains("reporting_analytics_events", sql);
    }
}
