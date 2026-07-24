using Dapper;
using Npgsql;
using Xunit;

namespace PrivacyAnalytics.IntegrationTests.Rls;

/// <summary>
/// Asserts that table-level grant restrictions on <c>erasure_audit_log</c> are enforced by PostgreSQL:
/// connecting as the non-superuser <c>analytics_app</c> role and attempting UPDATE, DELETE, or TRUNCATE
/// against <c>erasure_audit_log</c> must fail at the database engine level with a permission denied error (42501).
/// </summary>
[Collection("RlsTests")]
public sealed class ErasureAuditLogGrantTests
{
    private const string TestDbName = "analytics_erasure_grant_test";

    [PostgresRequiredFact]
    public async Task AnalyticsAppRole_UpdateDeleteTruncate_OnErasureAuditLog_ThrowsPermissionDenied()
    {
        var maintenanceCs = TestDatabaseHarness.ResolveMaintenanceConnectionString();

        await using var harness = await TestDatabaseHarness.CreateAsync(maintenanceCs, TestDbName);
        if (harness is null)
        {
            throw new InvalidOperationException(
                "PostgreSQL (TimescaleDB) was not reachable during provisioning. " +
                "Start the docker-compose stack before running this integration test.");
        }

        var tenantId = Guid.NewGuid();

        // 1. Seed organization and erasure audit log row using the Admin connection
        await using (var admin = new NpgsqlConnection(harness.AdminConnectionString))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync(
                "INSERT INTO organizations (org_id, name, public_write_key) VALUES (@Id, @Name, @Id)",
                new { Id = tenantId, Name = "Grant Test Tenant" });

            await admin.ExecuteAsync(
                """
                INSERT INTO erasure_audit_log (audit_id, organization_id, purged_identifier_hash, requested_by, purged_at_utc, records_affected)
                VALUES (gen_random_uuid(), @OrgId, 'test_hash_123', 'admin@granttest.com', NOW(), 1);
                """,
                new { OrgId = tenantId });
        }

        // 2. Connect as analytics_app role and attempt UPDATE -> expect PostgresException permission denied (SqlState 42501)
        await using (var appConn = new NpgsqlConnection(harness.AppConnectionString))
        {
            await appConn.OpenAsync();
            await appConn.ExecuteAsync($"SET app.current_tenant_id = '{tenantId}';");

            var updateEx = await Assert.ThrowsAsync<PostgresException>(() =>
                appConn.ExecuteAsync("UPDATE erasure_audit_log SET requested_by = 'hacker' WHERE organization_id = @OrgId", new { OrgId = tenantId }));
            Assert.Equal("42501", updateEx.SqlState);
        }

        // 3. Connect as analytics_app role and attempt DELETE -> expect PostgresException permission denied (SqlState 42501)
        await using (var appConn = new NpgsqlConnection(harness.AppConnectionString))
        {
            await appConn.OpenAsync();
            await appConn.ExecuteAsync($"SET app.current_tenant_id = '{tenantId}';");

            var deleteEx = await Assert.ThrowsAsync<PostgresException>(() =>
                appConn.ExecuteAsync("DELETE FROM erasure_audit_log WHERE organization_id = @OrgId", new { OrgId = tenantId }));
            Assert.Equal("42501", deleteEx.SqlState);
        }

        // 4. Connect as analytics_app role and attempt TRUNCATE -> expect PostgresException permission denied (SqlState 42501)
        await using (var appConn = new NpgsqlConnection(harness.AppConnectionString))
        {
            await appConn.OpenAsync();

            var truncateEx = await Assert.ThrowsAsync<PostgresException>(() =>
                appConn.ExecuteAsync("TRUNCATE erasure_audit_log;"));
            Assert.Equal("42501", truncateEx.SqlState);
        }
    }
}
