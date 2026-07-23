using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using PrivacyAnalytics.Domain.Identity;
using PrivacyAnalytics.Infrastructure.Ai;
using PrivacyAnalytics.Infrastructure.Analytics.Queries;
using PrivacyAnalytics.Infrastructure.Data;
using Xunit;

namespace PrivacyAnalytics.IntegrationTests.Rls;

/// <summary>
/// Milestone Test:
/// 1. A normal prompt ("top 5 pages this week") produces valid, executable SQL against the reporting views.
/// 2. A deliberately malicious query ("...; DROP TABLE analytics_events;--") gets rejected by shape validation before ever reaching the DB.
/// </summary>
[Collection("RlsTests")]
public sealed class AiQueryMilestoneTests
{
    private const string TestDbName = "ai_milestone_test";

    [PostgresRequiredFact]
    public async Task NormalPrompt_ProducesValidExecutableSql_AndMaliciousPrompt_IsRejectedByShapeValidation()
    {
        var maintenanceCs = TestDatabaseHarness.ResolveMaintenanceConnectionString();
        await using var harness = await TestDatabaseHarness.CreateAsync(maintenanceCs, TestDbName);
        Assert.NotNull(harness);

        var tenantId = Guid.NewGuid();
        await SeedTenantDataAsync(harness.AdminConnectionString, tenantId);

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(x => x.GetSection("ConnectionStrings")["AnalyticsDb"]).Returns(harness.AppConnectionString);

        var shapeValidator = new SqlShapeValidator();
        var httpClient = new HttpClient();
        var logger = NullLogger<OpenRouterTextToSqlService>.Instance;
        var aiService = new OpenRouterTextToSqlService(httpClient, shapeValidator, configMock.Object, logger);

        var tenantMock = new Mock<ICurrentTenant>();
        tenantMock.Setup(x => x.TenantId).Returns(tenantId);
        var dapperHelper = new DapperQueryHelper(configMock.Object, tenantMock.Object);

        var handler = new AskAiQueryHandler(aiService, dapperHelper);

        // 1. Normal prompt: "top 5 pages this week"
        var normalResult = await handler.Handle(
            new AskAiQuery("Show me the top 5 pages by unique visitors this week.", UseCache: true),
            CancellationToken.None);

        Assert.NotNull(normalResult);
        Assert.NotEmpty(normalResult.GeneratedSql);
        Assert.Contains("reporting_", normalResult.GeneratedSql); // Targets reporting views
        Assert.NotNull(normalResult.Data); // Executed successfully against database

        // 2. Malicious prompt injection: "...; DROP TABLE analytics_events;--"
        var maliciousSql = "SELECT * FROM reporting_analytics_events; DROP TABLE analytics_events;--";
        var (isValid, errorMessage) = shapeValidator.Validate(maliciousSql);

        Assert.False(isValid, "Malicious SQL should have been rejected by shape validation.");
        Assert.NotNull(errorMessage);
        Assert.Contains("Semicolons are only permitted at the very end", errorMessage);
    }

    private static async Task SeedTenantDataAsync(string adminConnectionString, Guid tenantId)
    {
        var baseTime = DateTimeOffset.UtcNow;

        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();

        await admin.ExecuteAsync(
            "INSERT INTO organizations (org_id, name, public_write_key) VALUES (@Id, @Name, @Id) ON CONFLICT (org_id) DO NOTHING;",
            new { Id = tenantId, Name = "Tenant Milestone" });

        await InsertEventAsync(admin, Guid.NewGuid(), tenantId, "hash_anon_1", null, false, "Pageview", "/home", baseTime);
        await InsertEventAsync(admin, Guid.NewGuid(), tenantId, null, "hash_durable_1", true, "Pageview", "/pricing", baseTime.AddSeconds(1));

        await admin.ExecuteAsync("GRANT SELECT ON ALL TABLES IN SCHEMA public TO analytics_app;");
    }

    private static async Task InsertEventAsync(NpgsqlConnection admin, Guid eventId, Guid orgId,
        string? anonHash, string? durableHash, bool isAuth,
        string eventType, string path, DateTimeOffset timestamp)
    {
        await admin.ExecuteAsync("""
            INSERT INTO analytics_events
                (event_id, organization_id, anonymous_daily_hash, durable_hash, is_authenticated, event_type, path, timestamp)
            VALUES
                (@EventId, @OrganizationId, @Anon, @Durable, @IsAuth, @EventType, @Path, @Timestamp);
            """,
            new 
            { 
                EventId = eventId, 
                OrganizationId = orgId, 
                Anon = anonHash,
                Durable = durableHash,
                IsAuth = isAuth,
                EventType = eventType, 
                Path = path, 
                Timestamp = timestamp 
            });
    }
}
