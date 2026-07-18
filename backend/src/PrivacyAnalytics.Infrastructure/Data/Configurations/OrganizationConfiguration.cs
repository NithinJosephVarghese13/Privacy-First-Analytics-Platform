using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PrivacyAnalytics.Domain.Entities;

namespace PrivacyAnalytics.Infrastructure.Data.Configurations;

public class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("org_id");

        builder.Property(o => o.Name)
            .HasColumnName("name")
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(o => o.PublicWriteKey)
            .HasColumnName("public_write_key")
            .IsRequired();

        builder.HasIndex(o => o.PublicWriteKey).IsUnique();
    }
}
