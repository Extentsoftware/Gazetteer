using Gazetteer.Core.Enums;
using Gazetteer.Core.Interfaces;
using Gazetteer.Core.Models;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Gazetteer.Seeder.Services;

public class HierarchyBuilder
{
    private readonly ILocationRepository _repository;
    private readonly ILogger<HierarchyBuilder> _logger;

    private static readonly HashSet<LocationType> AdminTypes =
    [
        LocationType.AdminRegion1,
        LocationType.AdminRegion2,
        LocationType.AdminRegion3
    ];

    public HierarchyBuilder(ILocationRepository repository, ILogger<HierarchyBuilder> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task BuildHierarchyAsync(string countryCode, CancellationToken ct = default)
    {
        _logger.LogInformation("Building hierarchy for {Country}...", countryCode);

        var allLocations = await _repository.GetByCountryAsync(countryCode, ct);
        var locationsWithGeometry = await _repository.GetLocationsWithGeometryAsync(countryCode, ct);

        var updates = new Dictionary<int, int>();

        BuildAdminRegionHierarchy(locationsWithGeometry, updates);
        AssignOrphansToAdminRegions(allLocations, locationsWithGeometry, updates);
        BuildProximityHierarchy(allLocations, updates);

        if (updates.Count > 0)
        {
            _logger.LogInformation("Applying {Count} parent assignments for {Country}", updates.Count, countryCode);
            await _repository.UpdateParentIdsAsync(updates, ct);
        }
    }

    private void BuildAdminRegionHierarchy(List<Location> locationsWithGeometry, Dictionary<int, int> updates)
    {
        var adminRegions = locationsWithGeometry
            .Where(l => AdminTypes.Contains(l.LocationType) && l.Geometry != null)
            .OrderBy(l => l.LocationType)
            .ToList();

        _logger.LogInformation("Building admin region hierarchy from {Count} regions with geometry", adminRegions.Count);

        for (int i = 0; i < adminRegions.Count; i++)
        {
            var child = adminRegions[i];
            if (child.LocationType == LocationType.AdminRegion1) continue;
            if (child.ParentId.HasValue) continue;

            var centroid = child.Geometry!.Centroid;
            Location? bestParent = null;
            double bestArea = double.MaxValue;

            foreach (var candidate in adminRegions)
            {
                if (candidate.Id == child.Id) continue;
                if (candidate.LocationType >= child.LocationType) continue;
                if (candidate.Geometry == null) continue;

                try
                {
                    if (candidate.Geometry.Contains(centroid) && candidate.Geometry.Area < bestArea)
                    {
                        bestParent = candidate;
                        bestArea = candidate.Geometry.Area;
                    }
                }
                catch { }
            }

            if (bestParent != null)
                updates[child.Id] = bestParent.Id;
        }
    }

    private void AssignOrphansToAdminRegions(
        List<Location> allLocations,
        List<Location> locationsWithGeometry,
        Dictionary<int, int> updates)
    {
        var adminPolygons = locationsWithGeometry
            .Where(l => AdminTypes.Contains(l.LocationType) && l.Geometry != null)
            .OrderByDescending(l => l.LocationType)
            .ToList();

        var orphans = allLocations
            .Where(l => l.ParentId == null && !updates.ContainsKey(l.Id) && !AdminTypes.Contains(l.LocationType))
            .ToList();

        _logger.LogInformation("Assigning {Count} orphan locations to admin regions", orphans.Count);

        foreach (var orphan in orphans)
        {
            var point = new Point(orphan.Lon, orphan.Lat) { SRID = 4326 };
            Location? bestParent = null;
            double bestArea = double.MaxValue;

            foreach (var admin in adminPolygons)
            {
                try
                {
                    if (admin.Geometry!.Contains(point) && admin.Geometry.Area < bestArea)
                    {
                        bestParent = admin;
                        bestArea = admin.Geometry.Area;
                    }
                }
                catch { }
            }

            if (bestParent != null)
                updates[orphan.Id] = bestParent.Id;
        }
    }

    private void BuildProximityHierarchy(List<Location> allLocations, Dictionary<int, int> updates)
    {
        var stillOrphans = allLocations
            .Where(l => l.ParentId == null && !updates.ContainsKey(l.Id)
                && l.LocationType != LocationType.Country
                && !AdminTypes.Contains(l.LocationType))
            .ToList();

        if (stillOrphans.Count == 0) return;

        _logger.LogInformation("Proximity fallback for {Count} remaining orphans", stillOrphans.Count);

        var adminRegions = allLocations
            .Where(l => AdminTypes.Contains(l.LocationType))
            .ToList();

        foreach (var orphan in stillOrphans)
        {
            Location? nearest = null;
            double nearestDist = double.MaxValue;

            foreach (var admin in adminRegions)
            {
                var dist = Math.Pow(orphan.Lat - admin.Lat, 2) + Math.Pow(orphan.Lon - admin.Lon, 2);
                if (dist < nearestDist)
                {
                    nearest = admin;
                    nearestDist = dist;
                }
            }

            if (nearest != null)
                updates[orphan.Id] = nearest.Id;
        }
    }
}
