using Gazetteer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gazetteer.Infrastructure.Data.Configurations;

public class LocationGroupConfiguration : IEntityTypeConfiguration<LocationGroup>
{
    public void Configure(EntityTypeBuilder<LocationGroup> builder)
    {
        builder.ToTable("location_groups");

        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).HasColumnName("id");
        builder.Property(g => g.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(g => g.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(g => g.UpdatedAtUtc).HasColumnName("updated_at_utc");

        builder.HasIndex(g => g.Name).IsUnique().HasDatabaseName("ix_location_groups_name");

        builder.HasMany(g => g.Members)
            .WithOne(m => m.Group)
            .HasForeignKey(m => m.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
