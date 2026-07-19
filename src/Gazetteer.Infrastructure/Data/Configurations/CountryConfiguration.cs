using Gazetteer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gazetteer.Infrastructure.Data.Configurations;

public class CountryConfiguration : IEntityTypeConfiguration<Country>
{
    public void Configure(EntityTypeBuilder<Country> builder)
    {
        builder.ToTable("countries");

        builder.HasKey(c => c.Code);
        builder.Property(c => c.Code).HasColumnName("code").HasMaxLength(2);
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(c => c.Continent).HasColumnName("continent").HasMaxLength(50);
        builder.Property(c => c.LastSeededFileTimestampUtc).HasColumnName("last_seeded_file_timestamp_utc");
        builder.Property(c => c.LastSeededAtUtc).HasColumnName("last_seeded_at_utc");
    }
}
