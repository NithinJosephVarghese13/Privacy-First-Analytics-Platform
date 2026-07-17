using Dapper;
using Npgsql;
using Xunit;

namespace PrivacyAnalytics.IntegrationTests.Rls;

/// <summary>
/// Proves the Phase 0 fail-closed isolation exit criterion: the Row-Level Security policy on
/// <c>analytics_events</c> filters on <c>organization_id = current_setting('app.current_tenant_id', true)::uuid</c>.
/// When the session variable is missing the policy returns zero rows (not an exception); when it
/// is set to a tenant only that tenant's rows are visible.
/// </summary>
/// <remarks>
/// AIRTIGHTNESS: the assertions run as the non-superuser TABLE OWNER (see <see cref="TestDatabaseHarness"/>).
/// An owner silently bypasses RLS unless <c>FORCE ROW LEVEL SECURITY</c> is set, so this test only stays
/// green while FORCE is in effect. Verified by toggling FORCE off: the missing-variable case then returns
/// all four seeded rows and the test fails. A guard assertion (<see cref="AssertEnforcesOwnerSubjectRlsAsync"/>)
/// also checks the relrowsecurity/relforcerowsecurity flags and table ownership up front so a future
/// regression (e.g. reverting to a non-owner role, for which FORCE is a no-op) fails fast with a clear message.
/// </remarks>
[Collection("RlsTests")]
public sealed class RlsTenantIsolationTests
{
    private const string TestDbName = "analytics_rls_test";

    [PostgresRequiredFact]
    public async Task MissingTenantVariable_ReturnsZeroRows_AndScopedVariable_ReturnsOnlyOwnRows()
    {
        var maintenanceCs = TestDatabaseHarness.ResolveMaintenanceConnectionString();

        await using var harness = await TestDatabaseHarness.CreateAsync(maintenanceCs, TestDbName);
        if (harness is null)
        {
            throw new InvalidOperationException(
                "PostgreSQL (TimescaleDB) was not reachable during provisioning. " +
                "Start the docker-compose stack before running this integration test.");
        }

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await SeedTwoTenantsAsync(harness.AdminConnectionString, tenantA, tenantB);

        // Guard: prove the test connection is the table owner and FORCE RLS is on. If this guard
        // ever fails, the behavioral assertions below would no longer be testing FORCE — they'd
        // pass for a non-owner regardless of FORCE (RLS always binds non-owners), masking a regression.
        await AssertEnforcesOwnerSubjectRlsAsync(harness.AdminConnectionString);

        // Phase 1 — fail-closed: a query with NO app.current_tenant_id set must return zero rows.
        // This must NOT raise (an error could be swallowed and falsely imply isolation). A missing
        // tenant context yields an empty result set because the USING clause evaluates to NULL.
        // The connection is held open so the session GUC state is not reset by the pooler between
        // RESET and the query. Because the connection is the table owner, only FORCE RLS keeps it
        // bound to the policy — this is the load-bearing guarantee under test.
        List<EventRow> unsetRows;
        await using (var app = new NpgsqlConnection(harness.AppConnectionString))
        {
            await app.OpenAsync();
            await app.ExecuteAsync("RESET app.current_tenant_id;"); // guarantee clean session state
            unsetRows = (await app.QueryAsync<EventRow>(QueryAllEventsSql)).ToList();
        }

        Assert.NotNull(unsetRows);
        Assert.Empty(unsetRows);

        // Phase 2 — scoped: setting app.current_tenant_id to tenant A must expose only tenant A's rows.
        List<EventRow> scopedRows;
        await using (var app = new NpgsqlConnection(harness.AppConnectionString))
        {
            await app.OpenAsync();
            await app.ExecuteAsync("RESET app.current_tenant_id;");
            await app.ExecuteAsync($"SET app.current_tenant_id = '{tenantA}';");
            scopedRows = (await app.QueryAsync<EventRow>(QueryAllEventsSql)).ToList();
        }

        Assert.NotNull(scopedRows);
        Assert.Equal(2, scopedRows.Count); // two events were seeded for tenant A
        Assert.All(scopedRows, row => Assert.Equal(tenantA, row.OrganizationId));
        Assert.DoesNotContain(scopedRows, row => row.OrganizationId == tenantB);
    }

    private const string QueryAllEventsSql = """
        SELECT event_id AS EventId,
               organization_id AS OrganizationId
        FROM analytics_events
        ORDER BY timestamp;
        """;

    private static async Task SeedTwoTenantsAsync(string adminConnectionString, Guid tenantA, Guid tenantB)
    {
        var baseTime = DateTimeOffset.UtcNow;

        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();

        // The superuser bypasses RLS, so seeding both tenants from here is not gated by the policy.
        await admin.ExecuteAsync(
            "INSERT INTO organizations (org_id, name) VALUES (@Id, @Name) ON CONFLICT (org_id) DO NOTHING;",
            new[]
            {
                new { Id = tenantA, Name = "Tenant A" },
                new { Id = tenantB, Name = "Tenant B" }
            });

        await InsertEventAsync(admin, Guid.NewGuid(), tenantA, "Pageview", "/home", baseTime);
        await InsertEventAsync(admin, Guid.NewGuid(), tenantA, "Click", "/home", baseTime.AddSeconds(1));
        await InsertEventAsync(admin, Guid.NewGuid(), tenantB, "Pageview", "/home", baseTime);
        await InsertEventAsync(admin, Guid.NewGuid(), tenantB, "Click", "/dashboard", baseTime.AddSeconds(1));

        // Re-grant SELECT so any hypertable chunks created during seeding are readable by the app role.
        await admin.ExecuteAsync("GRANT SELECT ON ALL TABLES IN SCHEMA public TO analytics_app;");
    }

    private static async Task InsertEventAsync(NpgsqlConnection admin, Guid eventId, Guid orgId,
        string eventType, string path, DateTimeOffset timestamp)
    {
        await admin.ExecuteAsync("""
            INSERT INTO analytics_events
                (event_id, organization_id, anonymous_daily_hash, durable_hash, is_authenticated, event_type, path, timestamp)
            VALUES
                (@EventId, @OrganizationId, NULL, NULL, FALSE, @EventType, @Path, @Timestamp);
            """,
            new { EventId = eventId, OrganizationId = orgId, EventType = eventType, Path = path, Timestamp = timestamp });
    }

    /// <summary>
    /// Verifies the invariant that makes this test airtight: the app role owns analytics_events
    /// and the table has FORCE ROW LEVEL SECURITY enabled. If the app role were not the owner, RLS
    /// would bind it regardless of FORCE and the behavioral assertions would no longer prove FORCE
    /// is in effect. Fails fast with a clear message on any regression.
    /// </summary>
    private static async Task AssertEnforcesOwnerSubjectRlsAsync(string adminConnectionString)
    {
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();

        var info = await admin.QuerySingleAsync<RlsTableInfo>("""
            SELECT c.relrowsecurity AS RlsEnabled,
                   c.relforcerowsecurity AS ForceRls,
                   r.rolname AS OwnerRole
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_roles r ON r.oid = c.relowner
            WHERE n.nspname = 'public' AND c.relname = 'analytics_events';
            """);

        Assert.NotNull(info);
        Assert.True(info.RlsEnabled,
            "analytics_events must have ROW LEVEL SECURITY enabled; otherwise the policy is a no-op.");
        Assert.True(info.ForceRls,
            "analytics_events must have FORCE ROW LEVEL SECURITY enabled. Without FORCE the table " +
            "owner bypasses RLS and the fail-closed guarantee is silently lost.");
        Assert.True(info.OwnerRole == "analytics_app",
            "The test connection role must own analytics_events. A non-owner is bound by RLS even " +
            "without FORCE, which would make this test pass while proving nothing about FORCE. " +
            $"Actual owner was '{info.OwnerRole}'.");
    }

    private sealed record RlsTableInfo(bool RlsEnabled, bool ForceRls, string OwnerRole);

    private sealed record EventRow(Guid EventId, Guid OrganizationId);
}
