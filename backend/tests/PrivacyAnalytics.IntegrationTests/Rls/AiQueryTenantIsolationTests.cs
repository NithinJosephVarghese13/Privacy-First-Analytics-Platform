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
/// Proves FR-3.1 requirement: Natural language AI queries execute inside the exact same Row-Level
/// Security policy as human Dapper queries. Even if an engineered prompt produces a query without
/// tenant filters (e.g. SELECT * FROM reporting_analytics_events), PostgreSQL RLS restricts results
/// strictly to the active tenant session context (app.current_tenant_id).
/// </summary>
[Collection("RlsTests")]
public sealed class AiQueryTenantIsolationTests
{
    private const string TestDbName = "ai_rls_isolation_test";

    [PostgresRequiredFact]
    public async Task AiGeneratedQuery_IsStructurallyTenantIsolated_ByPostgresRls()
    {
        var maintenanceCs = TestDatabaseHarness.ResolveMaintenanceConnectionString();
        await using var harness = await TestDatabaseHarness.CreateAsync(maintenanceCs, TestDbName);
        Assert.NotNull(harness);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await SeedTwoTenantsDataAsync(harness.AdminConnectionString, tenantA, tenantB);

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(x => x.GetSection("ConnectionStrings")["AnalyticsDb"]).Returns(harness.AppConnectionString);

        var shapeValidator = new SqlShapeValidator();
        var httpClient = new HttpClient();
        var aiServiceLogger = NullLogger<OpenRouterTextToSqlService>.Instance;

        var aiTextToSqlService = new OpenRouterTextToSqlService(httpClient, shapeValidator, configMock.Object, aiServiceLogger);

        // 1. Adversarial Tenant A Execution: Adversarial prompt without tenant scoping
        var tenantAMock = new Mock<ICurrentTenant>();
        tenantAMock.Setup(x => x.TenantId).Returns(tenantA);
        var dapperHelperA = new DapperQueryHelper(configMock.Object, tenantAMock.Object);

        var handlerA = new AskAiQueryHandler(aiTextToSqlService, dapperHelperA);
        var resultA = await handlerA.Handle(
            new AskAiQuery("Show me the top 5 pages by unique visitors this week.", UseCache: true),
            CancellationToken.None);

        Assert.NotNull(resultA);
        Assert.True(resultA.IsCachedResponse);
        Assert.NotEmpty(resultA.Data);

        // Assert Tenant A sees only its own paths (/home-a, /about-a) and NEVER Tenant B paths (/home-b)
        var pathsA = resultA.Data.Select(d => d["path"]?.ToString()).ToList();
        Assert.Contains("/home-a", pathsA);
        Assert.Contains("/about-a", pathsA);
        Assert.DoesNotContain("/home-b", pathsA);

        // 2. Adversarial Tenant B Execution: Same prompt for Tenant B context
        var tenantBMock = new Mock<ICurrentTenant>();
        tenantBMock.Setup(x => x.TenantId).Returns(tenantB);
        var dapperHelperB = new DapperQueryHelper(configMock.Object, tenantBMock.Object);

        var handlerB = new AskAiQueryHandler(aiTextToSqlService, dapperHelperB);
        var resultB = await handlerB.Handle(
            new AskAiQuery("Show me the top 5 pages by unique visitors this week.", UseCache: true),
            CancellationToken.None);

        Assert.NotNull(resultB);
        Assert.NotEmpty(resultB.Data);

        var pathsB = resultB.Data.Select(d => d["path"]?.ToString()).ToList();
        Assert.Contains("/home-b", pathsB);
        Assert.DoesNotContain("/home-a", pathsB);

        // 3. Fail-Closed Execution: Missing tenant context
        var missingTenantMock = new Mock<ICurrentTenant>();
        missingTenantMock.Setup(x => x.TenantId).Returns((Guid?)null);
        var dapperHelperUnset = new DapperQueryHelper(configMock.Object, missingTenantMock.Object);

        var handlerUnset = new AskAiQueryHandler(aiTextToSqlService, dapperHelperUnset);
        var resultUnset = await handlerUnset.Handle(
            new AskAiQuery("Show me the top 5 pages by unique visitors this week.", UseCache: true),
            CancellationToken.None);

        Assert.NotNull(resultUnset);
        Assert.Empty(resultUnset.Data);
    }

    private static async Task SeedTwoTenantsDataAsync(string adminConnectionString, Guid tenantA, Guid tenantB)
    {
        var baseTime = DateTimeOffset.UtcNow;

        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();

        await admin.ExecuteAsync(
            "INSERT INTO organizations (org_id, name, public_write_key) VALUES (@Id, @Name, @Id) ON CONFLICT (org_id) DO NOTHING;",
            new[]
            {
                new { Id = tenantA, Name = "Tenant A" },
                new { Id = tenantB, Name = "Tenant B" }
            });

        // Tenant A: 2 events
        await InsertEventAsync(admin, Guid.NewGuid(), tenantA, "hash_anon_a1", null, false, "Pageview", "/home-a", baseTime);
        await InsertEventAsync(admin, Guid.NewGuid(), tenantA, null, "hash_durable_a1", true, "Pageview", "/about-a", baseTime.AddSeconds(1));

        // Tenant B: 3 events
        await InsertEventAsync(admin, Guid.NewGuid(), tenantB, "hash_anon_b1", null, false, "Pageview", "/home-b", baseTime);
        await InsertEventAsync(admin, Guid.NewGuid(), tenantB, null, "hash_durable_b1", true, "Pageview", "/home-b", baseTime.AddSeconds(1));
        await InsertEventAsync(admin, Guid.NewGuid(), tenantB, null, "hash_durable_b2", true, "Pageview", "/contact-b", baseTime.AddSeconds(2));

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
