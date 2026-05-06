using Gazetteer.Core.Models;
using Gazetteer.Infrastructure.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Gazetteer.Infrastructure.Data;

public class GazetteerDbContext : DbContext
{
    public GazetteerDbContext(DbContextOptions<GazetteerDbContext> options) : base(options) { }

    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Country> Countries => Set<Country>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.HasPostgresExtension("unaccent");

        modelBuilder.ApplyConfiguration(new LocationConfiguration());
        modelBuilder.ApplyConfiguration(new CountryConfiguration());
    }
}
