using Gazetteer.Core.Enums;
using Gazetteer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;

namespace Gazetteer.Seeder.Services;

public class HierarchyBuilder
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HierarchyBuilder> _logger;

    public HierarchyBuilder(IServiceProvider serviceProvider, ILogger<HierarchyBuilder> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task BuildHierarchyAsync(string countryCode, CancellationToken ct = default)
    {
        _logger.LogInformation("Building hierarchy for country: {Country}", countryCode);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();

        // Get admin regions sorted by level (largest first)
        var adminRegions = await db.Locations
            .Where(l => l.CountryCode == countryCode &&
                        (l.LocationType == LocationType.Country ||
                         l.LocationType == LocationType.AdminRegion1 ||
                         l.LocationType == LocationType.AdminRegion2 ||
                         l.LocationType == LocationType.AdminRegion3))
            .Where(l => l.Geometry != null)
            .OrderBy(l => l.LocationType)
            .ToListAsync(ct);

        _logger.LogInformation("Found {Count} admin regions with geometry for {Country}",
            adminRegions.Count, countryCode);

        if (adminRegions.Count == 0)
        {
            _logger.LogWarning("No admin regions with geometry found for {Country}. " +
                "Falling back to proximity-based hierarchy.", countryCode);
            await BuildProximityHierarchyAsync(db, countryCode, ct);
            return;
        }

        // First: build hierarchy among admin regions themselves
        await BuildAdminRegionHierarchyAsync(db, adminRegions, countryCode, ct);

        // Then: assign all other orphan locations to their containing admin region
        await AssignOrphansToAdminRegionsAsync(db, adminRegions, countryCode, ct);

        // Finally: assign roads/amenities to nearest sub-locality (neighborhood/village/town)
        // so the hierarchy shows e.g. Spur Road → Goddington → London Borough of Bromley
        await AssignToNearestSubLocalityAsync(countryCode, ct);
    }

    /// <summary>
    /// Build parent-child relationships between admin regions using spatial containment.
    /// E.g., AdminRegion2 contained by AdminRegion1, AdminRegion1 contained by Country.
    /// </summary>
    private async Task BuildAdminRegionHierarchyAsync(
        GazetteerDbContext db, List<Core.Models.Location> adminRegions,
        string countryCode, CancellationToken ct)
    {
        // Pre-validate and prepare geometries for fast containment checks
        var preparedRegions = PrepareGeometries(adminRegions);
        int assigned = 0;

        foreach (var region in adminRegions.Where(r => r.LocationType != LocationType.Country && r.ParentId == null))
        {
            var centroid = region.Geometry!.Centroid;
            var point = new Point(centroid.X, centroid.Y) { SRID = 4326 };

            var parent = adminRegions
                .Where(r => r.LocationType < region.LocationType && r.Geometry != null)
                .OrderByDescending(r => r.LocationType)
                .FirstOrDefault(r => SafeContains(preparedRegions, r.Id, point));

            if (parent != null)
            {
                region.ParentId = parent.Id;
                assigned++;
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Admin region hierarchy for {Country}: assigned {Count} parents",
            countryCode, assigned);
    }

    /// <summary>
    /// For each non-admin orphan location, find the smallest containing admin region.
    /// Uses the location's lat/lon point against admin region polygon geometries.
    /// Processes in batches to avoid loading millions of entities into a single DbContext.
    /// </summary>
    private async Task AssignOrphansToAdminRegionsAsync(
        GazetteerDbContext db, List<Core.Models.Location> adminRegions,
        string countryCode, CancellationToken ct)
    {
        // Count orphans without loading them
        var totalOrphans = await db.Locations
            .Where(l => l.CountryCode == countryCode && l.ParentId == null && l.LocationType != LocationType.Country)
            .Where(l => l.LocationType != LocationType.AdminRegion1 &&
                        l.LocationType != LocationType.AdminRegion2 &&
                        l.LocationType != LocationType.AdminRegion3)
            .CountAsync(ct);

        _logger.LogInformation("Assigning parents to {Count:N0} orphan locations in {Country}", totalOrphans, countryCode);

        var preparedRegions = PrepareGeometries(adminRegions);
        int totalAssigned = 0;
        int totalProcessed = 0;
        const int batchSize = 25_000;
        long lastId = 0;

        while (totalProcessed < totalOrphans)
        {
            // Use a fresh scope per batch to keep change tracker small
            using var batchScope = _serviceProvider.CreateScope();
            var batchDb = batchScope.ServiceProvider.GetRequiredService<GazetteerDbContext>();
            batchDb.ChangeTracker.AutoDetectChangesEnabled = false;

            // Keyset pagination: fetch next batch of orphans ordered by Id
            var batch = await batchDb.Locations
                .Where(l => l.CountryCode == countryCode && l.ParentId == null && l.LocationType != LocationType.Country)
                .Where(l => l.LocationType != LocationType.AdminRegion1 &&
                            l.LocationType != LocationType.AdminRegion2 &&
                            l.LocationType != LocationType.AdminRegion3)
                .Where(l => l.Id > lastId)
                .OrderBy(l => l.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            int batchAssigned = 0;
            foreach (var location in batch)
            {
                if (location.Latitude == 0 && location.Longitude == 0) continue;

                var point = new Point(location.Longitude, location.Latitude) { SRID = 4326 };

                var parent = adminRegions
                    .Where(r => r.LocationType < location.LocationType && r.Geometry != null)
                    .OrderByDescending(r => r.LocationType)
                    .FirstOrDefault(r => SafeContains(preparedRegions, r.Id, point));

                if (parent != null)
                {
                    location.ParentId = parent.Id;
                    batchAssigned++;
                }
            }

            batchDb.ChangeTracker.DetectChanges();
            await batchDb.SaveChangesAsync(ct);

            totalAssigned += batchAssigned;
            totalProcessed += batch.Count;
            _logger.LogInformation("Hierarchy progress: {Processed:N0}/{Total:N0} processed, {Assigned:N0} assigned",
                totalProcessed, totalOrphans, totalAssigned);
        }

        _logger.LogInformation("Hierarchy complete for {Country}: {Assigned:N0} of {Total:N0} locations assigned parents",
            countryCode, totalAssigned, totalOrphans);
    }

    /// <summary>
    /// For roads and amenities already assigned to an admin region, find the nearest
    /// neighborhood/village/town/city within the same admin region and re-parent through it.
    /// E.g.: Road → AdminRegion3 becomes Road → Neighborhood → AdminRegion3
    /// </summary>
    private async Task AssignToNearestSubLocalityAsync(string countryCode, CancellationToken ct)
    {
        _logger.LogInformation("Assigning sub-locality parents for roads/amenities in {Country}", countryCode);

        // Load all sub-localities (neighborhoods, villages, towns, cities) that have parents
        using var lookupScope = _serviceProvider.CreateScope();
        var lookupDb = lookupScope.ServiceProvider.GetRequiredService<GazetteerDbContext>();

        var subLocalities = await lookupDb.Locations
            .AsNoTracking()
            .Where(l => l.CountryCode == countryCode && l.ParentId != null)
            .Where(l => l.LocationType == LocationType.Neighborhood ||
                        l.LocationType == LocationType.Village ||
                        l.LocationType == LocationType.Town ||
                        l.LocationType == LocationType.City ||
                        l.LocationType == LocationType.Locality)
            .Select(l => new { l.Id, l.ParentId, l.Latitude, l.Longitude, l.LocationType })
            .ToListAsync(ct);

        if (subLocalities.Count == 0)
        {
            _logger.LogInformation("No sub-localities found for {Country}, skipping", countryCode);
            return;
        }

        // Group sub-localities by their admin parent for fast lookup
        var subLocalitiesByParent = subLocalities
            .Where(s => s.ParentId.HasValue)
            .GroupBy(s => s.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        _logger.LogInformation("Found {Count:N0} sub-localities across {Parents:N0} admin regions",
            subLocalities.Count, subLocalitiesByParent.Count);

        // Process roads/amenities in batches
        const int batchSize = 25_000;
        long lastId = 0;
        int totalAssigned = 0;
        int totalProcessed = 0;

        while (true)
        {
            using var batchScope = _serviceProvider.CreateScope();
            var batchDb = batchScope.ServiceProvider.GetRequiredService<GazetteerDbContext>();
            batchDb.ChangeTracker.AutoDetectChangesEnabled = false;

            var batch = await batchDb.Locations
                .Where(l => l.CountryCode == countryCode && l.ParentId != null)
                .Where(l => l.LocationType == LocationType.Road || l.LocationType == LocationType.Amenity)
                .Where(l => l.Id > lastId)
                .OrderBy(l => l.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            int batchAssigned = 0;
            foreach (var location in batch)
            {
                if (location.Latitude == 0 && location.Longitude == 0) continue;
                if (!location.ParentId.HasValue) continue;

                // Find sub-localities under the same admin parent
                if (!subLocalitiesByParent.TryGetValue(location.ParentId.Value, out var candidates))
                    continue;

                // Find nearest sub-locality by distance
                double bestDist = double.MaxValue;
                long? bestId = null;

                foreach (var candidate in candidates)
                {
                    if (candidate.Latitude == 0 && candidate.Longitude == 0) continue;
                    var dist = Distance(location.Latitude, location.Longitude, candidate.Latitude, candidate.Longitude);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestId = candidate.Id;
                    }
                }

                // Only assign if within ~5km (0.05 degrees squared ≈ 5km at mid-latitudes)
                if (bestId.HasValue && bestDist < 0.0025)
                {
                    location.ParentId = bestId.Value;
                    batchAssigned++;
                }
            }

            batchDb.ChangeTracker.DetectChanges();
            await batchDb.SaveChangesAsync(ct);

            totalAssigned += batchAssigned;
            totalProcessed += batch.Count;

            if (totalProcessed % 100_000 == 0 || batch.Count < batchSize)
                _logger.LogInformation("Sub-locality assignment: {Processed:N0} processed, {Assigned:N0} assigned",
                    totalProcessed, totalAssigned);
        }

        _logger.LogInformation("Sub-locality assignment complete for {Country}: {Assigned:N0} of {Total:N0} roads/amenities assigned",
            countryCode, totalAssigned, totalProcessed);
    }

    private async Task BuildProximityHierarchyAsync(GazetteerDbContext db, string countryCode, CancellationToken ct)
    {
        // Fallback: assign parent based on nearest higher-level location
        var country = await db.Locations
            .FirstOrDefaultAsync(l => l.CountryCode == countryCode && l.LocationType == LocationType.Country, ct);

        if (country == null)
        {
            _logger.LogWarning("No country-level location found for {Country}", countryCode);
            return;
        }

        // Assign all AdminRegion1 to country
        var region1s = await db.Locations
            .Where(l => l.CountryCode == countryCode && l.LocationType == LocationType.AdminRegion1 && l.ParentId == null)
            .ToListAsync(ct);
        foreach (var r in region1s) r.ParentId = country.Id;

        // Assign AdminRegion2 to nearest AdminRegion1
        await AssignToNearestParentAsync(db, countryCode, LocationType.AdminRegion2, LocationType.AdminRegion1, ct);

        // Assign cities/towns/villages to nearest admin region
        var childTypes = new[] { LocationType.City, LocationType.Town, LocationType.Village,
                                  LocationType.Neighborhood, LocationType.Locality,
                                  LocationType.Road, LocationType.Postcode };

        foreach (var childType in childTypes)
        {
            await AssignToNearestParentAsync(db, countryCode, childType, null, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task AssignToNearestParentAsync(
        GazetteerDbContext db, string countryCode,
        LocationType childType, LocationType? parentType,
        CancellationToken ct)
    {
        var children = await db.Locations
            .Where(l => l.CountryCode == countryCode && l.LocationType == childType && l.ParentId == null)
            .ToListAsync(ct);

        if (children.Count == 0) return;

        // Get potential parents (any higher-level location)
        var parents = await db.Locations
            .Where(l => l.CountryCode == countryCode &&
                        (parentType.HasValue ? l.LocationType == parentType.Value : l.LocationType < childType) &&
                        l.LocationType != LocationType.Road && l.LocationType != LocationType.Postcode)
            .AsNoTracking()
            .ToListAsync(ct);

        if (parents.Count == 0) return;

        foreach (var child in children)
        {
            var nearest = parents
                .OrderBy(p => Distance(child.Latitude, child.Longitude, p.Latitude, p.Longitude))
                .FirstOrDefault();

            if (nearest != null)
                child.ParentId = nearest.Id;
        }

        _logger.LogInformation("Assigned {Count} {Type} locations to nearest parent in {Country}",
            children.Count, childType, countryCode);
    }

    /// <summary>
    /// Pre-validates geometries with MakeValid() and builds PreparedGeometry for fast containment checks.
    /// </summary>
    private Dictionary<long, IPreparedGeometry> PrepareGeometries(List<Core.Models.Location> regions)
    {
        var factory = new PreparedGeometryFactory();
        var result = new Dictionary<long, IPreparedGeometry>();

        foreach (var region in regions.Where(r => r.Geometry != null))
        {
            try
            {
                var geom = region.Geometry!;
                if (!geom.IsValid)
                    geom = geom.Buffer(0); // fast way to fix most invalid geometries

                result[region.Id] = factory.Create(geom);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Skipping invalid geometry for {Name} (ID {Id}): {Error}",
                    region.Name, region.Id, ex.Message);
            }
        }

        return result;
    }

    private static bool SafeContains(Dictionary<long, IPreparedGeometry> prepared, long regionId, Point point)
    {
        if (!prepared.TryGetValue(regionId, out var geom))
            return false;

        try
        {
            return geom.Contains(point);
        }
        catch
        {
            return false;
        }
    }

    private static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        var dlat = lat2 - lat1;
        var dlon = lon2 - lon1;
        return dlat * dlat + dlon * dlon; // Squared Euclidean distance (sufficient for ranking)
    }
}
