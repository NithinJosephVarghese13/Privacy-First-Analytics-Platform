using Microsoft.EntityFrameworkCore;
using PrivacyAnalytics.Domain.Entities;

namespace PrivacyAnalytics.Infrastructure.Data;

public class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<AnalyticsEvent> AnalyticsEvents => Set<AnalyticsEvent>();
    public DbSet<ErasureAuditLog> ErasureAuditLogs => Set<ErasureAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AnalyticsDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ThrowIfErasureAuditLogModified();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ThrowIfErasureAuditLogModified();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ThrowIfErasureAuditLogModified()
    {
        var modifiedOrDeleted = ChangeTracker.Entries<ErasureAuditLog>()
            .Where(e => e.State is EntityState.Modified or EntityState.Deleted)
            .ToList();
        if (modifiedOrDeleted.Count != 0)
            throw new InvalidOperationException("ErasureAuditLog is insert-only. Updates and deletes are forbidden.");
    }
}
