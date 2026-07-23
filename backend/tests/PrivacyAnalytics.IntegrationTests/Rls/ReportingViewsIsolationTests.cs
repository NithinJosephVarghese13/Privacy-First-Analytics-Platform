using Dapper;
using Npgsql;
using Xunit;

namespace PrivacyAnalytics.IntegrationTests.Rls;

/// <summary>
/// Verifies that the restricted reporting views (reporting_analytics_events, reporting_daily_pageviews,
/// reporting_top_pages) expose only aggregate-safe columns (no raw hashes or PII fields) and inherit
/// the underlying table's Row-Level Security policy via WITH (security_invoker = true).
/// </summary>
[Collection("RlsTests")]
public sealed class ReportingViewsIsolationTests
{
    private const string TestDbName = "reporting_views_rls_test";

    [PostgresRequiredFact]
    public async Task ReportingViews_DoNotExposeRawHashes_AndEnforceRlsTenantIsolation()
    {
        var maintenanceCs = TestDatabaseHarness.ResolveMaintenanceConnectionString();
        await using var harness = await TestDatabaseHarness.CreateAsync(maintenanceCs, TestDbName);
        Assert.NotNull(harness);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await SeedTwoTenantsDataAsync(harness.AdminConnectionString, tenantA, tenantB);

        // 1. Schema Assertion: Confirm reporting views exist and DO NOT expose raw hash columns
        await using (var admin = new NpgsqlConnection(harness.AdminConnectionString))
        {
            await admin.OpenAsync();

            var viewColumns = (await admin.QueryAsync<string>("""
                SELECT column_name
                FROM information_schema.columns
                WHERE table_name = 'reporting_analytics_events';
                """)).ToList();

            Assert.NotEmpty(viewColumns);
            Assert.Contains("event_id", viewColumns);
            Assert.Contains("organization_id", viewColumns);
            Assert.Contains("timestamp", viewColumns);
            Assert.Contains("event_type", viewColumns);
            Assert.Contains("path", viewColumns);
            Assert.Contains("is_authenticated", viewColumns);

            // Crucial privacy guarantee: raw hashes must NOT be exposed in the view
            Assert.DoesNotContain("anonymous_daily_hash", viewColumns);
            Assert.DoesNotContain("durable_hash", viewColumns);
        }

        // 2. Fail-Closed Isolation Assertion: Unset session variable must return zero rows from views
        await using (var app = new NpgsqlConnection(harness.AppConnectionString))
        {
            await app.OpenAsync();
            await app.ExecuteAsync("RESET app.current_tenant_id;");

            var unsetEvents = (await app.QueryAsync<ReportingEventRow>("""
                SELECT event_id AS EventId, organization_id AS OrganizationId, path AS Path
                FROM reporting_analytics_events;
                """)).ToList();

            Assert.Empty(unsetEvents);

            var unsetDaily = (await app.QueryAsync<dynamic>("""
                SELECT * FROM reporting_daily_pageviews;
                """)).ToList();

            Assert.Empty(unsetDaily);

            var unsetTop = (await app.QueryAsync<dynamic>("""
                SELECT * FROM reporting_top_pages;
                """)).ToList();

            Assert.Empty(unsetTop);
        }

        // 3. Scoped Isolation Assertion for Tenant A: Setting app.current_tenant_id exposes only Tenant A's rows
        await using (var app = new NpgsqlConnection(harness.AppConnectionString))
        {
            await app.OpenAsync();
            await app.ExecuteAsync("RESET app.current_tenant_id;");
            await app.ExecuteAsync($"SET app.current_tenant_id = '{tenantA}';");

            var eventsA = (await app.QueryAsync<ReportingEventRow>("""
                SELECT event_id AS EventId, organization_id AS OrganizationId, path AS Path, event_type AS EventType, is_authenticated AS IsAuthenticated
                FROM reporting_analytics_events;
                """)).ToList();

            Assert.Equal(2, eventsA.Count);
            Assert.All(eventsA, e => Assert.Equal(tenantA, e.OrganizationId));
            Assert.Contains(eventsA, e => e.Path == "/home-a");
            Assert.Contains(eventsA, e => e.Path == "/about-a");
            Assert.DoesNotContain(eventsA, e => e.Path == "/home-b");

            var topA = (await app.QueryAsync<ReportingTopPageRow>("""
                SELECT path AS Path, total_events AS TotalEvents
                FROM reporting_top_pages;
                """)).ToList();

            Assert.Equal(2, topA.Count);
            Assert.All(topA, p => Assert.DoesNotContain("home-b", p.Path));
        }

        // 4. Scoped Isolation Assertion for Tenant B: Setting app.current_tenant_id exposes only Tenant B's rows
        await using (var app = new NpgsqlConnection(harness.AppConnectionString))
        {
            await app.OpenAsync();
            await app.ExecuteAsync("RESET app.current_tenant_id;");
            await app.ExecuteAsync($"SET app.current_tenant_id = '{tenantB}';");

            var eventsB = (await app.QueryAsync<ReportingEventRow>("""
                SELECT event_id AS EventId, organization_id AS OrganizationId, path AS Path
                FROM reporting_analytics_events;
                """)).ToList();

            Assert.Equal(3, eventsB.Count);
            Assert.All(eventsB, e => Assert.Equal(tenantB, e.OrganizationId));
            Assert.DoesNotContain(eventsB, e => e.Path == "/home-a");

            var topB = (await app.QueryAsync<ReportingTopPageRow>("""
                SELECT path AS Path, total_events AS TotalEvents
                FROM reporting_top_pages;
                """)).ToList();

            Assert.Contains(topB, p => p.Path == "/home-b" && p.TotalEvents == 2);
            Assert.Contains(topB, p => p.Path == "/contact-b" && p.TotalEvents == 1);
        }
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

    private sealed class ReportingEventRow
    {
        public Guid EventId { get; init; }
        public Guid OrganizationId { get; init; }
        public string Path { get; init; } = string.Empty;
        public string EventType { get; init; } = string.Empty;
        public bool IsAuthenticated { get; init; }
    }

    private sealed class ReportingTopPageRow
    {
        public string Path { get; init; } = string.Empty;
        public long TotalEvents { get; init; }
    }
}
