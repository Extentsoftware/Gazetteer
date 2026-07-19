using Gazetteer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gazetteer.Infrastructure.Data.Configurations;

public class LocationGroupMemberConfiguration : IEntityTypeConfiguration<LocationGroupMember>
{
    public void Configure(EntityTypeBuilder<LocationGroupMember> builder)
    {
        builder.ToTable("location_group_members");

        builder.HasKey(m => new { m.GroupId, m.LocationType });
        builder.Property(m => m.GroupId).HasColumnName("group_id");
        builder.Property(m => m.LocationType).HasColumnName("location_type").HasConversion<string>().HasMaxLength(50);
        builder.Property(m => m.Rank).HasColumnName("rank");
        builder.Property(m => m.Boost).HasColumnName("boost");

        builder.HasIndex(m => new { m.GroupId, m.Rank }).HasDatabaseName("ix_location_group_members_group_rank");
    }
}
