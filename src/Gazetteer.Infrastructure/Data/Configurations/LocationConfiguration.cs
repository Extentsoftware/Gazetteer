using Gazetteer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gazetteer.Infrastructure.Data.Configurations;

public class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        builder.ToTable("locations");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnName("id");
        builder.Property(l => l.OsmId).HasColumnName("osm_id");
        builder.Property(l => l.OsmType).HasColumnName("osm_type").HasConversion<string>();
        builder.Property(l => l.Name).HasColumnName("name").HasMaxLength(500).IsRequired();
        builder.Property(l => l.LocalName).HasColumnName("local_name").HasMaxLength(500);
        builder.Property(l => l.NameEn).HasColumnName("name_en").HasMaxLength(500);
        builder.Property(l => l.AlternateNames).HasColumnName("alternate_names").HasColumnType("text");
        builder.Property(l => l.LocationType).HasColumnName("location_type").HasConversion<string>();
        builder.Property(l => l.SubType).HasColumnName("sub_type").HasMaxLength(100);
        builder.Property(l => l.CountryCode).HasColumnName("country_code").HasMaxLength(2).IsRequired();
        builder.Property(l => l.Latitude).HasColumnName("latitude");
        builder.Property(l => l.Longitude).HasColumnName("longitude");
        builder.Property(l => l.Geometry).HasColumnName("geometry").HasColumnType("geometry");
        builder.Property(l => l.Population).HasColumnName("population");
        builder.Property(l => l.PostalCode).HasColumnName("postal_code").HasColumnType("text");
        builder.Property(l => l.ParentId).HasColumnName("parent_id");

        builder.HasOne(l => l.Parent)
            .WithMany(l => l.Children)
            .HasForeignKey(l => l.ParentId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(l => l.OsmId).HasDatabaseName("ix_locations_osm_id");
        builder.HasIndex(l => l.CountryCode).HasDatabaseName("ix_locations_country_code");
        builder.HasIndex(l => l.LocationType).HasDatabaseName("ix_locations_location_type");
        builder.HasIndex(l => l.ParentId).HasDatabaseName("ix_locations_parent_id");
        builder.HasIndex(l => l.Name).HasDatabaseName("ix_locations_name_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
    }
}
