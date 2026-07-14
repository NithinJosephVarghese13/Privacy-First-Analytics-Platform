using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PrivacyAnalytics.Domain.Entities;

namespace PrivacyAnalytics.Infrastructure.Data.Configurations;

public class ErasureAuditLogConfiguration : IEntityTypeConfiguration<ErasureAuditLog>
{
    public void Configure(EntityTypeBuilder<ErasureAuditLog> builder)
    {
        builder.ToTable("erasure_audit_log");

        builder.HasKey(e => e.AuditId);
        builder.Property(e => e.AuditId).HasColumnName("audit_id");

        builder.Property(e => e.OrganizationId).HasColumnName("organization_id");

        builder.Property(e => e.PurgedIdentifierHash)
            .HasColumnName("purged_identifier_hash")
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(e => e.RequestedBy)
            .HasColumnName("requested_by")
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.PurgedAtUtc).HasColumnName("purged_at_utc");
        builder.Property(e => e.RecordsAffected).HasColumnName("records_affected");

        builder.HasIndex(e => e.OrganizationId).HasDatabaseName("ix_erasure_audit_log_organization_id");
    }
}
