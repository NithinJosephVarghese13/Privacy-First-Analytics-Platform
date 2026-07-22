using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PrivacyAnalytics.Domain.Entities;
using PrivacyAnalytics.Domain.Identity;
using PrivacyAnalytics.Infrastructure.Analytics.Commands;
using PrivacyAnalytics.Infrastructure.Data;
using Xunit;

namespace PrivacyAnalytics.UnitTests.Analytics;

public class PurgeTier2DataCommandHandlerTests
{
    private class TestCurrentTenant(Guid? tenantId) : ICurrentTenant
    {
        public Guid? TenantId => tenantId;
    }

    private static AnalyticsDbContext CreateSqliteDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseSqlite(connection)
            .Options;
        var context = new AnalyticsDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    [Fact]
    public async Task Handle_WithoutTenantContext_ThrowsInvalidOperationException()
    {
        // Arrange
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        using var dbContext = CreateSqliteDbContext(connection);

        var tenantService = new TestCurrentTenant(null);
        var handler = new PurgeTier2DataCommandHandler(dbContext, tenantService);
        var command = new PurgeTier2DataCommand("durable-hash-123", "Admin");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(command, CancellationToken.None));
        Assert.Contains("Tenant context is missing", ex.Message);
    }

    [Fact]
    public async Task Handle_WithEmptyDurableHash_ThrowsArgumentException()
    {
        // Arrange
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        using var dbContext = CreateSqliteDbContext(connection);

        var tenantId = Guid.NewGuid();
        var tenantService = new TestCurrentTenant(tenantId);
        var handler = new PurgeTier2DataCommandHandler(dbContext, tenantService);
        var command = new PurgeTier2DataCommand("", "Admin");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_WithMatchingRecords_DeletesEventsAndInsertsAuditLog()
    {
        // Arrange
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        using var dbContext = CreateSqliteDbContext(connection);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var durableHashTarget = "target-durable-hash";
        var durableHashOther = "other-durable-hash";

        // Seed orgs
        dbContext.Organizations.Add(new Organization { Id = tenantA, Name = "Tenant A" });
        dbContext.Organizations.Add(new Organization { Id = tenantB, Name = "Tenant B" });

        // Seed events
        dbContext.AnalyticsEvents.AddRange(
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(),
                OrganizationId = tenantA,
                DurableHash = durableHashTarget,
                IsAuthenticated = true,
                EventType = "pageview",
                Path = "/dashboard",
                Timestamp = DateTimeOffset.UtcNow
            },
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(),
                OrganizationId = tenantA,
                DurableHash = durableHashTarget,
                IsAuthenticated = true,
                EventType = "click",
                Path = "/button",
                Timestamp = DateTimeOffset.UtcNow
            },
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(),
                OrganizationId = tenantA,
                DurableHash = durableHashOther,
                IsAuthenticated = true,
                EventType = "pageview",
                Path = "/home",
                Timestamp = DateTimeOffset.UtcNow
            },
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(),
                OrganizationId = tenantB,
                DurableHash = durableHashTarget,
                IsAuthenticated = true,
                EventType = "pageview",
                Path = "/dashboard",
                Timestamp = DateTimeOffset.UtcNow
            }
        );
        await dbContext.SaveChangesAsync();

        var tenantService = new TestCurrentTenant(tenantA);
        var handler = new PurgeTier2DataCommandHandler(dbContext, tenantService);
        var command = new PurgeTier2DataCommand(durableHashTarget, "dpo@tenantA.com");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(tenantA, result.OrganizationId);
        Assert.Equal(durableHashTarget, result.PurgedIdentifierHash);
        Assert.Equal("dpo@tenantA.com", result.RequestedBy);
        Assert.Equal(2, result.RecordsAffected);

        // Verify database state
        var tenantAEvents = await dbContext.AnalyticsEvents.Where(e => e.OrganizationId == tenantA).ToListAsync();
        Assert.Single(tenantAEvents);
        Assert.Equal(durableHashOther, tenantAEvents[0].DurableHash);

        // Verify Tenant B untouched
        var tenantBEvents = await dbContext.AnalyticsEvents.Where(e => e.OrganizationId == tenantB).ToListAsync();
        Assert.Single(tenantBEvents);

        // Verify audit log recorded
        var auditLogs = await dbContext.ErasureAuditLogs.Where(a => a.OrganizationId == tenantA).ToListAsync();
        Assert.Single(auditLogs);
        Assert.Equal(2, auditLogs[0].RecordsAffected);
        Assert.Equal(durableHashTarget, auditLogs[0].PurgedIdentifierHash);
    }
}
