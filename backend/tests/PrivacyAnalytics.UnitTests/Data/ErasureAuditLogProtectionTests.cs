using Microsoft.EntityFrameworkCore;
using PrivacyAnalytics.Domain.Entities;
using PrivacyAnalytics.Infrastructure.Data;
using Xunit;

namespace PrivacyAnalytics.UnitTests.Data;

public class ErasureAuditLogProtectionTests
{
    private AnalyticsDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AnalyticsDbContext(options);
    }

    [Fact]
    public async Task ErasureAuditLog_Add_Succeeds()
    {
        // Arrange
        using var dbContext = CreateDbContext();
        var auditEntry = new ErasureAuditLog
        {
            AuditId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(),
            PurgedIdentifierHash = "durable-hash-123",
            RequestedBy = "admin@example.com",
            PurgedAtUtc = DateTimeOffset.UtcNow,
            RecordsAffected = 5
        };

        // Act
        dbContext.ErasureAuditLogs.Add(auditEntry);
        var result = await dbContext.SaveChangesAsync();

        // Assert
        Assert.Equal(1, result);
        var saved = await dbContext.ErasureAuditLogs.FirstOrDefaultAsync(x => x.AuditId == auditEntry.AuditId);
        Assert.NotNull(saved);
        Assert.Equal("durable-hash-123", saved.PurgedIdentifierHash);
    }

    [Fact]
    public async Task ErasureAuditLog_Update_ThrowsInvalidOperationException()
    {
        // Arrange
        using var dbContext = CreateDbContext();
        var auditEntry = new ErasureAuditLog
        {
            AuditId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(),
            PurgedIdentifierHash = "durable-hash-123",
            RequestedBy = "admin@example.com",
            PurgedAtUtc = DateTimeOffset.UtcNow,
            RecordsAffected = 5
        };
        dbContext.ErasureAuditLogs.Add(auditEntry);
        await dbContext.SaveChangesAsync();

        // Act & Assert
        auditEntry.RequestedBy = "unauthorized-editor@example.com";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());
        Assert.Contains("ErasureAuditLog is insert-only", ex.Message);
    }

    [Fact]
    public async Task ErasureAuditLog_Delete_ThrowsInvalidOperationException()
    {
        // Arrange
        using var dbContext = CreateDbContext();
        var auditEntry = new ErasureAuditLog
        {
            AuditId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(),
            PurgedIdentifierHash = "durable-hash-123",
            RequestedBy = "admin@example.com",
            PurgedAtUtc = DateTimeOffset.UtcNow,
            RecordsAffected = 5
        };
        dbContext.ErasureAuditLogs.Add(auditEntry);
        await dbContext.SaveChangesAsync();

        // Act & Assert
        dbContext.ErasureAuditLogs.Remove(auditEntry);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());
        Assert.Contains("ErasureAuditLog is insert-only", ex.Message);
    }
}
