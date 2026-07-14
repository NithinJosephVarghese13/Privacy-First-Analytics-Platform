using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PrivacyAnalytics.Domain.Entities;

namespace PrivacyAnalytics.Infrastructure.Data.Configurations;

public class AnalyticsEventConfiguration : IEntityTypeConfiguration<AnalyticsEvent>
{
    public void Configure(EntityTypeBuilder<AnalyticsEvent> builder)
    {
        builder.ToTable("analytics_events");

        builder.HasKey(e => new { e.EventId, e.Timestamp });
        builder.Property(e => e.EventId).HasColumnName("event_id");

        builder.Property(e => e.OrganizationId).HasColumnName("organization_id");

        builder.Property(e => e.AnonymousDailyHash)
            .HasColumnName("anonymous_daily_hash")
            .HasMaxLength(128);

        builder.Property(e => e.DurableHash)
            .HasColumnName("durable_hash")
            .HasMaxLength(128);

        builder.Property(e => e.IsAuthenticated).HasColumnName("is_authenticated");

        builder.Property(e => e.EventType)
            .HasColumnName("event_type")
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(e => e.Path)
            .HasColumnName("path")
            .IsRequired()
            .HasMaxLength(2048);

        builder.Property(e => e.Timestamp).HasColumnName("timestamp");

        builder.HasIndex(e => e.OrganizationId).HasDatabaseName("ix_analytics_events_organization_id");
        builder.HasIndex(e => e.Timestamp).HasDatabaseName("ix_analytics_events_timestamp_desc").IsDescending();
    }
}
