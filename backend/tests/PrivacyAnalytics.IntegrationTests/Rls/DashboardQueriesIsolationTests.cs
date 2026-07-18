using Microsoft.Extensions.Configuration;
using Moq;
using Npgsql;
using PrivacyAnalytics.Infrastructure.Analytics.Queries;
using PrivacyAnalytics.Domain.Identity;
using PrivacyAnalytics.Infrastructure.Data;
using System.Data;
using Xunit;
using Dapper;

namespace PrivacyAnalytics.IntegrationTests.Rls;

[Collection("RlsTests")]
public class DashboardQueriesIsolationTests
{
    private const string TestDbName = "dashboard_rls_test";

    [PostgresRequiredFact]
    public async Task DashboardQueries_AreIsolatedByTenant_AndFailClosedWhenMissing()
    {
        var maintenanceCs = TestDatabaseHarness.ResolveMaintenanceConnectionString();
        await using var harness = await TestDatabaseHarness.CreateAsync(maintenanceCs, TestDbName);
        Assert.NotNull(harness);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await SeedTwoTenantsDataAsync(harness.AdminConnectionString, tenantA, tenantB);

        // 1. Missing ICurrentTenant Context (Fail-Closed)
        var missingContextMock = new Mock<ICurrentTenant>();
        missingContextMock.Setup(x => x.TenantId).Returns((Guid?)null);

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(x => x.GetSection("ConnectionStrings")["AnalyticsDb"]).Returns(harness.AppConnectionString);

        var helperWithMissingContext = new DapperQueryHelper(configMock.Object, missingContextMock.Object);

        // We run a Dashboard Query using the handler with missing context
        var pageviewsHandlerMissing = new GetPageviewsOverTimeQueryHandler(helperWithMissingContext);
        var missingResult = await pageviewsHandlerMissing.Handle(
            new GetPageviewsOverTimeQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), 
            CancellationToken.None);

        Assert.Empty(missingResult);

        var uniqueVisitorsHandlerMissing = new GetUniqueVisitorsQueryHandler(helperWithMissingContext);
        var missingUniques = await uniqueVisitorsHandlerMissing.Handle(
            new GetUniqueVisitorsQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), 
            CancellationToken.None);
        
        Assert.Equal(0, missingUniques.ExactTier2Uniques);
        Assert.Equal(0, missingUniques.EstimatedTier1Uniques);

        // 2. Dashboard Queries for Tenant A
        var tenantAMock = new Mock<ICurrentTenant>();
        tenantAMock.Setup(x => x.TenantId).Returns(tenantA);
        var helperForA = new DapperQueryHelper(configMock.Object, tenantAMock.Object);

        var pageviewsHandlerA = new GetPageviewsOverTimeQueryHandler(helperForA);
        var topPagesHandlerA = new GetTopPagesQueryHandler(helperForA);
        var uniqueVisitorsHandlerA = new GetUniqueVisitorsQueryHandler(helperForA);

        var pageviewsA = await pageviewsHandlerA.Handle(
            new GetPageviewsOverTimeQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), 
            CancellationToken.None);
        
        // Tenant A has 2 pageviews (seeded)
        Assert.Equal(2, pageviewsA.Sum(p => p.Pageviews));

        var topPagesA = await topPagesHandlerA.Handle(
            new GetTopPagesQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), 
            CancellationToken.None);
        
        Assert.Contains(topPagesA, p => p.Path == "/home-a" && p.Pageviews == 1);
        Assert.Contains(topPagesA, p => p.Path == "/about-a" && p.Pageviews == 1);
        Assert.DoesNotContain(topPagesA, p => p.Path == "/home-b");

        var uniquesA = await uniqueVisitorsHandlerA.Handle(
            new GetUniqueVisitorsQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), 
            CancellationToken.None);
        
        Assert.Equal(1, uniquesA.ExactTier2Uniques);

        // 3. Dashboard Queries for Tenant B
        var tenantBMock = new Mock<ICurrentTenant>();
        tenantBMock.Setup(x => x.TenantId).Returns(tenantB);
        var helperForB = new DapperQueryHelper(configMock.Object, tenantBMock.Object);

        var pageviewsHandlerB = new GetPageviewsOverTimeQueryHandler(helperForB);
        var topPagesHandlerB = new GetTopPagesQueryHandler(helperForB);
        var uniqueVisitorsHandlerB = new GetUniqueVisitorsQueryHandler(helperForB);

        var pageviewsB = await pageviewsHandlerB.Handle(
            new GetPageviewsOverTimeQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), 
            CancellationToken.None);
        
        Assert.Equal(3, pageviewsB.Sum(p => p.Pageviews)); // Tenant B has 3 pageviews

        var topPagesB = await topPagesHandlerB.Handle(
            new GetTopPagesQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), 
            CancellationToken.None);
        
        Assert.Contains(topPagesB, p => p.Path == "/home-b" && p.Pageviews == 2);
        Assert.DoesNotContain(topPagesB, p => p.Path == "/home-a");
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

        // Tenant A: 2 events, 1 durable hash, 1 anonymous hash
        await InsertEventAsync(admin, Guid.NewGuid(), tenantA, "hash_anon_a1", null, false, "Pageview", "/home-a", baseTime);
        await InsertEventAsync(admin, Guid.NewGuid(), tenantA, null, "hash_durable_a1", true, "Pageview", "/about-a", baseTime.AddSeconds(1));

        // Tenant B: 3 events, 2 durable hashes, 1 anonymous hash
        await InsertEventAsync(admin, Guid.NewGuid(), tenantB, "hash_anon_b1", null, false, "Pageview", "/home-b", baseTime);
        await InsertEventAsync(admin, Guid.NewGuid(), tenantB, null, "hash_durable_b1", true, "Pageview", "/home-b", baseTime.AddSeconds(1));
        await InsertEventAsync(admin, Guid.NewGuid(), tenantB, null, "hash_durable_b2", true, "Pageview", "/contact-b", baseTime.AddSeconds(2));

        // Create HLL extension in test db if not already done, just to be sure
        await admin.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS hll;");
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
