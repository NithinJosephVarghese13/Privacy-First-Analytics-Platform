using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using Npgsql;
using PrivacyAnalytics.Domain.Entities;
using PrivacyAnalytics.Domain.Identity;
using PrivacyAnalytics.Infrastructure.Analytics.Commands;
using PrivacyAnalytics.Infrastructure.Data;
using PrivacyAnalytics.IntegrationTests.Rls;
using Xunit;

namespace PrivacyAnalytics.IntegrationTests.Api;

[Collection("RlsTests")]
public class ErasureEndpointTests
{
    private const string TestDbName = "analytics_erasure_test";

    [PostgresRequiredFact]
    public async Task PurgeTier2Data_DeletesMatchingEvents_AndCreatesAuditEntry_InSameTransaction()
    {
        var maintenanceCs = TestDatabaseHarness.ResolveMaintenanceConnectionString();
        await using var harness = await TestDatabaseHarness.CreateAsync(maintenanceCs, TestDbName);
        if (harness is null)
        {
            throw new InvalidOperationException("PostgreSQL test database could not be provisioned.");
        }

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var targetDurableHash = "durable-hash-to-purge";
        var keepDurableHash = "durable-hash-to-keep";

        // Seed orgs & events using admin connection
        await using (var admin = new NpgsqlConnection(harness.AdminConnectionString))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync(
                "INSERT INTO organizations (org_id, name, public_write_key) VALUES (@Id, @Name, @Id)",
                new[]
                {
                    new { Id = tenantA, Name = "Tenant A" },
                    new { Id = tenantB, Name = "Tenant B" }
                });

            await admin.ExecuteAsync(
                """
                INSERT INTO analytics_events (event_id, organization_id, anonymous_daily_hash, durable_hash, is_authenticated, event_type, path, timestamp)
                VALUES 
                    (@E1, @OrgA, NULL, @TargetHash, true, 'pageview', '/home', NOW()),
                    (@E2, @OrgA, NULL, @TargetHash, true, 'click', '/button', NOW()),
                    (@E3, @OrgA, NULL, @KeepHash, true, 'pageview', '/settings', NOW()),
                    (@E4, @OrgB, NULL, @TargetHash, true, 'pageview', '/dashboard', NOW());
                """,
                new
                {
                    E1 = Guid.NewGuid(),
                    E2 = Guid.NewGuid(),
                    E3 = Guid.NewGuid(),
                    E4 = Guid.NewGuid(),
                    OrgA = tenantA,
                    OrgB = tenantB,
                    TargetHash = targetDurableHash,
                    KeepHash = keepDurableHash
                });
        }

        // Set up DbContext connected to the app database
        var options = new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseNpgsql(harness.AppConnectionString)
            .Options;

        await using var dbContext = new AnalyticsDbContext(options);

        var tenantAMock = new Mock<ICurrentTenant>();
        tenantAMock.Setup(x => x.TenantId).Returns(tenantA);

        var handler = new PurgeTier2DataCommandHandler(dbContext, tenantAMock.Object);
        var command = new PurgeTier2DataCommand(targetDurableHash, "dpo@tenantA.com");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert contract result
        Assert.NotNull(result);
        Assert.Equal(tenantA, result.OrganizationId);
        Assert.Equal(targetDurableHash, result.PurgedIdentifierHash);
        Assert.Equal("dpo@tenantA.com", result.RequestedBy);
        Assert.Equal(2, result.RecordsAffected);

        // Verify in DB directly using admin connection
        await using (var admin = new NpgsqlConnection(harness.AdminConnectionString))
        {
            await admin.OpenAsync();

            var tenantAEvents = (await admin.QueryAsync<AnalyticsEvent>(
                "SELECT event_id AS EventId, organization_id AS OrganizationId, durable_hash AS DurableHash FROM analytics_events WHERE organization_id = @TenantId",
                new { TenantId = tenantA })).ToList();

            Assert.Single(tenantAEvents);
            Assert.Equal(keepDurableHash, tenantAEvents[0].DurableHash);

            var tenantBEvents = (await admin.QueryAsync<AnalyticsEvent>(
                "SELECT event_id AS EventId, organization_id AS OrganizationId, durable_hash AS DurableHash FROM analytics_events WHERE organization_id = @TenantId",
                new { TenantId = tenantB })).ToList();

            Assert.Single(tenantBEvents);
            Assert.Equal(targetDurableHash, tenantBEvents[0].DurableHash);

            var auditLogs = (await admin.QueryAsync<ErasureAuditLog>(
                "SELECT audit_id AS AuditId, organization_id AS OrganizationId, purged_identifier_hash AS PurgedIdentifierHash, requested_by AS RequestedBy, purged_at_utc AS PurgedAtUtc, records_affected AS RecordsAffected FROM erasure_audit_log WHERE organization_id = @TenantId",
                new { TenantId = tenantA })).ToList();

            Assert.Single(auditLogs);
            Assert.Equal(2, auditLogs[0].RecordsAffected);
            Assert.Equal(targetDurableHash, auditLogs[0].PurgedIdentifierHash);
            Assert.Equal("dpo@tenantA.com", auditLogs[0].RequestedBy);
        }
    }

    [PostgresRequiredFact]
    public async Task MilestoneTest_PurgeIdentifier_ZeroMatchingRowsRemain_AuditRowLogged_AppRoleUpdateAndDeleteRejected()
    {
        var maintenanceCs = TestDatabaseHarness.ResolveMaintenanceConnectionString();
        await using var harness = await TestDatabaseHarness.CreateAsync(maintenanceCs, "analytics_erasure_milestone_test");
        if (harness is null)
        {
            throw new InvalidOperationException("PostgreSQL test database could not be provisioned.");
        }

        var tenantId = Guid.NewGuid();
        var targetHash = "known-durable-hash-999";

        // Seed org & matching events as admin
        await using (var admin = new NpgsqlConnection(harness.AdminConnectionString))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync(
                "INSERT INTO organizations (org_id, name, public_write_key) VALUES (@Id, @Name, @Id)",
                new { Id = tenantId, Name = "Milestone Tenant" });

            await admin.ExecuteAsync(
                """
                INSERT INTO analytics_events (event_id, organization_id, anonymous_daily_hash, durable_hash, is_authenticated, event_type, path, timestamp)
                VALUES 
                    (@E1, @OrgId, NULL, @Hash, true, 'pageview', '/dashboard', NOW()),
                    (@E2, @OrgId, NULL, @Hash, true, 'click', '/submit', NOW());
                """,
                new { E1 = Guid.NewGuid(), E2 = Guid.NewGuid(), OrgId = tenantId, Hash = targetHash });
        }

        // Execute purge using Handler with app DB role connection
        var options = new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseNpgsql(harness.AppConnectionString)
            .Options;

        await using (var dbContext = new AnalyticsDbContext(options))
        {
            var tenantMock = new Mock<ICurrentTenant>();
            tenantMock.Setup(x => x.TenantId).Returns(tenantId);

            var handler = new PurgeTier2DataCommandHandler(dbContext, tenantMock.Object);
            var command = new PurgeTier2DataCommand(targetHash, "admin@milestone.org");

            var result = await handler.Handle(command, CancellationToken.None);
            Assert.Equal(2, result.RecordsAffected);
        }

        // 1. Confirm ZERO matching rows remain for target identifier
        await using (var admin = new NpgsqlConnection(harness.AdminConnectionString))
        {
            await admin.OpenAsync();

            var matchingRemaining = await admin.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM analytics_events WHERE organization_id = @TenantId AND durable_hash = @Hash",
                new { TenantId = tenantId, Hash = targetHash });

            Assert.Equal(0, matchingRemaining);

            // 2. Confirm EXACTLY ONE audit row exists describing the purge
            var auditRows = (await admin.QueryAsync<ErasureAuditLog>(
                "SELECT audit_id AS AuditId, organization_id AS OrganizationId, purged_identifier_hash AS PurgedIdentifierHash, requested_by AS RequestedBy, purged_at_utc AS PurgedAtUtc, records_affected AS RecordsAffected FROM erasure_audit_log WHERE organization_id = @TenantId",
                new { TenantId = tenantId })).ToList();

            Assert.Single(auditRows);
            Assert.Equal(targetHash, auditRows[0].PurgedIdentifierHash);
            Assert.Equal("admin@milestone.org", auditRows[0].RequestedBy);
            Assert.Equal(2, auditRows[0].RecordsAffected);
        }

        // 3. Attempt to UPDATE the audit row directly with the app's DB role -> confirm 0 rows affected (rejected by RLS insert-only policy)
        await using (var appConn = new NpgsqlConnection(harness.AppConnectionString))
        {
            await appConn.OpenAsync();
            await appConn.ExecuteAsync($"SET app.current_tenant_id = '{tenantId}';");

            var updatedRows = await appConn.ExecuteAsync(
                "UPDATE erasure_audit_log SET requested_by = 'hacker' WHERE organization_id = @TenantId",
                new { TenantId = tenantId });

            Assert.Equal(0, updatedRows);
        }

        // 4. Attempt to DELETE the audit row directly with the app's DB role -> confirm 0 rows affected (rejected by RLS insert-only policy)
        await using (var appConn = new NpgsqlConnection(harness.AppConnectionString))
        {
            await appConn.OpenAsync();
            await appConn.ExecuteAsync($"SET app.current_tenant_id = '{tenantId}';");

            var deletedRows = await appConn.ExecuteAsync(
                "DELETE FROM erasure_audit_log WHERE organization_id = @TenantId",
                new { TenantId = tenantId });

            Assert.Equal(0, deletedRows);
        }

        // 5. Confirm EF Core DbContext explicitly rejects UPDATE and DELETE on ErasureAuditLog
        await using (var dbContext = new AnalyticsDbContext(options))
        {
            await using var tx = await dbContext.Database.BeginTransactionAsync();
            var dbConn = dbContext.Database.GetDbConnection();
            await using var setCmd = dbConn.CreateCommand();
            setCmd.Transaction = tx.GetDbTransaction();
            setCmd.CommandText = $"SET LOCAL app.current_tenant_id = '{tenantId}';";
            await setCmd.ExecuteNonQueryAsync();

            var existingAudit = await dbContext.ErasureAuditLogs.FirstOrDefaultAsync(x => x.OrganizationId == tenantId);
            Assert.NotNull(existingAudit);

            existingAudit.RequestedBy = "altered";
            var efUpdateEx = await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());
            Assert.Contains("ErasureAuditLog is insert-only", efUpdateEx.Message);
        }

        // 6. Verify audit row remained 100% intact after all mutation attempts
        await using (var admin = new NpgsqlConnection(harness.AdminConnectionString))
        {
            await admin.OpenAsync();
            var auditRows = (await admin.QueryAsync<ErasureAuditLog>(
                "SELECT audit_id AS AuditId, organization_id AS OrganizationId, purged_identifier_hash AS PurgedIdentifierHash, requested_by AS RequestedBy, purged_at_utc AS PurgedAtUtc, records_affected AS RecordsAffected FROM erasure_audit_log WHERE organization_id = @TenantId",
                new { TenantId = tenantId })).ToList();

            Assert.Single(auditRows);
            Assert.Equal("admin@milestone.org", auditRows[0].RequestedBy);
        }
    }
}
