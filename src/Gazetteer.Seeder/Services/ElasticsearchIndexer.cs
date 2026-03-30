using Gazetteer.Core.Enums;
using Gazetteer.Core.Interfaces;
using Gazetteer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetTopologySuite.IO;
using System.Text.Json;

namespace Gazetteer.Seeder.Services;

public class ElasticsearchIndexer
{
    private record ParentLookupEntry(long OsmId, string Name, string? NameEn, string? LocalName, string LocationType, long? ParentId);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ElasticsearchIndexer> _logger;

    private static readonly HashSet<LocationType> AdminTypes =
    [
        LocationType.Country,
        LocationType.AdminRegion1,
        LocationType.AdminRegion2,
        LocationType.AdminRegion3
    ];

    private static readonly Dictionary<LocationType, int> AdminLevelMap = new()
    {
        [LocationType.Country] = 2,
        [LocationType.AdminRegion1] = 4,
        [LocationType.AdminRegion2] = 6,
        [LocationType.AdminRegion3] = 8,
    };

    public ElasticsearchIndexer(IServiceProvider serviceProvider, ILogger<ElasticsearchIndexer> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    // Locality types used for building searchable address (nearby place names)
    private static readonly HashSet<LocationType> LocalityTypes =
    [
        LocationType.Neighborhood,
        LocationType.Village,
        LocationType.Town,
        LocationType.City,
        LocationType.Locality
    ];

    public async Task IndexAllAsync(int batchSize, bool recreateIndex, CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var esService = scope.ServiceProvider.GetRequiredService<IElasticsearchService>();
        var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();

        if (recreateIndex)
        {
            _logger.LogInformation("Deleting existing Elasticsearch indices...");
            await esService.DeleteIndexAsync(ct);
            await esService.DeleteBoundariesIndexAsync(ct);
        }

        await esService.CreateIndexAsync(ct);
        await esService.CreateBoundariesIndexAsync(ct);

        // Pre-load a lookup of all locations for building parent chains
        _logger.LogInformation("Loading parent lookup...");
        var parentLookup = await db.Locations
            .AsNoTracking()
            .Select(l => new { l.Id, Entry = new ParentLookupEntry(l.OsmId, l.Name, l.NameEn, l.LocalName, l.LocationType.ToString(), l.ParentId) })
            .ToDictionaryAsync(l => l.Id, l => l.Entry, ct);

        _logger.LogInformation("Parent lookup loaded: {Count:N0} entries", parentLookup.Count);

        // Pre-load localities grouped by admin parent for locality fields
        _logger.LogInformation("Loading locality lookup for searchable address...");
        var localityLookup = await db.Locations
            .AsNoTracking()
            .Where(l => LocalityTypes.Contains(l.LocationType) && l.ParentId != null)
            .Select(l => new { l.Name, l.ParentId, l.Latitude, l.Longitude })
            .ToListAsync(ct);

        // Also load single-word amenities (stations, etc. often named after areas)
        // e.g. "Wallington" station, "Orpington" station — these are area names people use
        var areaAmenities = await db.Locations
            .AsNoTracking()
            .Where(l => l.LocationType == LocationType.Amenity && l.ParentId != null)
            .Where(l => !EF.Functions.Like(l.Name, "% %"))
            .Select(l => new { l.Name, l.ParentId, l.Latitude, l.Longitude })
            .ToListAsync(ct);

        _logger.LogInformation("Loaded {Amenities:N0} single-word amenities as area names", areaAmenities.Count);
        localityLookup.AddRange(areaAmenities);

        // Group by admin parent → list of (name, lat, lon)
        var localitiesByParent = localityLookup
            .Where(l => l.ParentId.HasValue)
            .GroupBy(l => l.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(l => (l.Name, l.Latitude, l.Longitude)).Distinct().ToList());

        _logger.LogInformation("Locality lookup loaded: {Count:N0} localities across {Parents:N0} admin regions",
            localityLookup.Count, localitiesByParent.Count);

        // Pre-load postcodes grouped by admin parent for nearby postcode lookup
        _logger.LogInformation("Loading postcode lookup for nearby postcodes...");
        var postcodeLookup = await db.Locations
            .AsNoTracking()
            .Where(l => l.LocationType == LocationType.Postcode && l.ParentId != null && l.PostalCode != null)
            .Select(l => new { l.PostalCode, l.ParentId, l.Latitude, l.Longitude })
            .ToListAsync(ct);

        var postcodesByParent = postcodeLookup
            .Where(l => l.ParentId.HasValue)
            .GroupBy(l => l.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(l => (l.PostalCode!, l.Latitude, l.Longitude)).ToList());

        _logger.LogInformation("Postcode lookup loaded: {Count:N0} postcodes across {Parents:N0} admin regions",
            postcodeLookup.Count, postcodesByParent.Count);

        await IndexLocationsAsync(esService, db, parentLookup, localitiesByParent, postcodesByParent, batchSize, ct);
        await IndexBoundariesAsync(esService, db, parentLookup, batchSize, ct);

        _logger.LogInformation("Elasticsearch indexing complete");
    }

    private List<ParentInfo> BuildParentsList(long? parentId, Dictionary<long, ParentLookupEntry> lookup)
    {
        var parents = new List<ParentInfo>();
        var visited = new HashSet<long>();
        var currentId = parentId;

        while (currentId.HasValue && visited.Add(currentId.Value))
        {
            if (!lookup.TryGetValue(currentId.Value, out var parent))
                break;

            parents.Add(new ParentInfo
            {
                OsmId = parent.OsmId,
                Name = parent.Name,
                NameEn = parent.NameEn,
                LocalName = parent.LocalName,
                LocationType = parent.LocationType
            });

            currentId = parent.ParentId;
        }

        return parents;
    }

    private static string? BuildParentChain(List<ParentInfo> parents)
    {
        if (parents.Count == 0) return null;
        return string.Join(" > ", parents.Select(p => p.Name));
    }

    /// <summary>
    /// Build distance-banded locality fields for search disambiguation.
    /// Close (~0-1.5km) localities get higher search boost than near (~1.5-5km) ones.
    /// </summary>
    private static (string? Close, string? Near) BuildLocalityFields(
        double lat, double lon, long? parentId,
        Dictionary<long, ParentLookupEntry> parentLookup,
        Dictionary<long, List<(string Name, double Latitude, double Longitude)>> localitiesByParent)
    {
        var closeParts = new List<string>();
        var nearParts = new List<string>();

        // Walk up the parent chain to find the admin region that localities are grouped by
        if (parentId.HasValue)
        {
            long? adminParentId = parentId.Value;
            while (adminParentId.HasValue && !localitiesByParent.ContainsKey(adminParentId.Value))
            {
                if (parentLookup.TryGetValue(adminParentId.Value, out var entry))
                    adminParentId = entry.ParentId;
                else
                    break;
            }

            if (adminParentId.HasValue && localitiesByParent.TryGetValue(adminParentId.Value, out var localities))
            {
                foreach (var loc in localities)
                {
                    if (loc.Latitude == 0 && loc.Longitude == 0) continue;
                    var dlat = lat - loc.Latitude;
                    var dlon = lon - loc.Longitude;
                    var distSq = dlat * dlat + dlon * dlon;

                    // ~1.5km at mid-latitudes ≈ 0.00020 squared degrees
                    if (distSq < 0.00020)
                    {
                        if (!closeParts.Contains(loc.Name))
                            closeParts.Add(loc.Name);
                    }
                    // ~1.5-5km
                    else if (distSq < 0.0025)
                    {
                        if (!nearParts.Contains(loc.Name))
                            nearParts.Add(loc.Name);
                    }
                }
            }
        }

        return (
            closeParts.Count > 0 ? string.Join(" ", closeParts) : null,
            nearParts.Count > 0 ? string.Join(" ", nearParts) : null
        );
    }

    /// <summary>
    /// Find postcodes within ~300m of a location for display and search.
    /// </summary>
    private static List<string> FindNearbyPostcodes(
        double lat, double lon, long? parentId,
        Dictionary<long, ParentLookupEntry> parentLookup,
        Dictionary<long, List<(string Postcode, double Latitude, double Longitude)>> postcodesByParent)
    {
        var result = new List<string>();

        if (!parentId.HasValue) return result;

        // Walk up to admin parent (same pattern as BuildLocalityFields)
        long? adminParentId = parentId.Value;
        while (adminParentId.HasValue && !postcodesByParent.ContainsKey(adminParentId.Value))
        {
            if (parentLookup.TryGetValue(adminParentId.Value, out var entry))
                adminParentId = entry.ParentId;
            else
                break;
        }

        if (!adminParentId.HasValue || !postcodesByParent.TryGetValue(adminParentId.Value, out var postcodes))
            return result;

        foreach (var pc in postcodes)
        {
            if (pc.Latitude == 0 && pc.Longitude == 0) continue;
            var dlat = lat - pc.Latitude;
            var dlon = lon - pc.Longitude;
            var distSq = dlat * dlat + dlon * dlon;

            // ~300m at mid-latitudes ≈ 0.000010 squared degrees
            if (distSq < 0.000010 && !result.Contains(pc.Postcode))
            {
                result.Add(pc.Postcode);
                if (result.Count >= 20) break; // cap to avoid bloating docs
            }
        }

        return result;
    }

    private async Task IndexLocationsAsync(
        IElasticsearchService esService, GazetteerDbContext db,
        Dictionary<long, ParentLookupEntry> parentLookup,
        Dictionary<long, List<(string Name, double Latitude, double Longitude)>> localitiesByParent,
        Dictionary<long, List<(string Postcode, double Latitude, double Longitude)>> postcodesByParent,
        int batchSize, CancellationToken ct)
    {
        var totalCount = await db.Locations.CountAsync(ct);
        _logger.LogInformation("Indexing {Count:N0} locations to Elasticsearch...", totalCount);

        int offset = 0;
        long indexed = 0;

        while (offset < totalCount)
        {
            var locations = await db.Locations
                .AsNoTracking()
                .OrderBy(l => l.Id)
                .Skip(offset)
                .Take(batchSize)
                .Select(l => new
                {
                    l.Id,
                    l.OsmId,
                    l.Name,
                    l.NameEn,
                    l.AlternateNames,
                    LocationType = l.LocationType.ToString(),
                    l.CountryCode,
                    l.SubType,
                    l.PostalCode,
                    l.Latitude,
                    l.Longitude,
                    l.Population,
                    HasGeometry = l.Geometry != null,
                    l.ParentId
                })
                .ToListAsync(ct);

            var documents = locations.Select(l =>
            {
                var parents = BuildParentsList(l.ParentId, parentLookup);
                var (close, near) = BuildLocalityFields(
                    l.Latitude, l.Longitude, l.ParentId, parentLookup, localitiesByParent);
                return new LocationIndexDocument
                {
                    Id = l.Id,
                    OsmId = l.OsmId,
                    Name = l.Name,
                    NameEn = l.NameEn,
                    AlternateNames = l.AlternateNames,
                    LocationType = l.LocationType,
                    SubType = l.SubType,
                    CountryCode = l.CountryCode,
                    PostalCode = l.PostalCode,
                    Latitude = l.Latitude,
                    Longitude = l.Longitude,
                    Population = l.Population,
                    HasGeometry = l.HasGeometry,
                    ParentChain = BuildParentChain(parents),
                    LocalitiesClose = close,
                    LocalitiesNear = near,
                    NearbyPostcodes = FindNearbyPostcodes(
                        l.Latitude, l.Longitude, l.ParentId, parentLookup, postcodesByParent),
                    Parents = parents
                };
            }).ToList();

            await esService.BulkIndexAsync(documents, ct);
            indexed += documents.Count;
            offset += batchSize;

            _logger.LogInformation("Indexed {Indexed:N0} / {Total:N0} locations", indexed, totalCount);
        }

        _logger.LogInformation("Location indexing complete: {Count:N0} documents", indexed);
    }

    private async Task IndexBoundariesAsync(
        IElasticsearchService esService, GazetteerDbContext db,
        Dictionary<long, ParentLookupEntry> parentLookup,
        int batchSize, CancellationToken ct)
    {
        var totalCount = await db.Locations
            .Where(l => l.Geometry != null && AdminTypes.Contains(l.LocationType))
            .CountAsync(ct);

        _logger.LogInformation("Indexing {Count:N0} boundary polygons to Elasticsearch...", totalCount);

        if (totalCount == 0)
        {
            _logger.LogWarning("No locations with geometry found for boundary indexing");
            return;
        }

        var geoJsonWriter = new GeoJsonWriter();
        int offset = 0;
        long indexed = 0;

        while (offset < totalCount)
        {
            var locations = await db.Locations
                .AsNoTracking()
                .Where(l => l.Geometry != null && AdminTypes.Contains(l.LocationType))
                .OrderBy(l => l.Id)
                .Skip(offset)
                .Take(batchSize)
                .Select(l => new
                {
                    l.Id,
                    l.OsmId,
                    l.Name,
                    l.NameEn,
                    l.LocationType,
                    l.CountryCode,
                    l.Latitude,
                    l.Longitude,
                    l.Population,
                    l.Geometry,
                    l.ParentId
                })
                .ToListAsync(ct);

            var documents = new List<BoundaryIndexDocument>();
            foreach (var l in locations)
            {
                if (l.Geometry == null) continue;

                object? geoShape;
                try
                {
                    var geoJson = geoJsonWriter.Write(l.Geometry);
                    geoShape = JsonSerializer.Deserialize<object>(geoJson);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Skipping boundary {Id} ({Name}): {Error}", l.Id, l.Name, ex.Message);
                    continue;
                }

                if (geoShape == null) continue;

                var parents = BuildParentsList(l.ParentId, parentLookup);
                documents.Add(new BoundaryIndexDocument
                {
                    Id = l.Id,
                    OsmId = l.OsmId,
                    Name = l.Name,
                    NameEn = l.NameEn,
                    LocationType = l.LocationType.ToString(),
                    CountryCode = l.CountryCode,
                    AdminLevel = AdminLevelMap.GetValueOrDefault(l.LocationType, 0),
                    Latitude = l.Latitude,
                    Longitude = l.Longitude,
                    Population = l.Population,
                    ParentChain = BuildParentChain(parents),
                    Parents = parents,
                    Boundary = geoShape
                });
            }

            if (documents.Count > 0)
                await esService.BulkIndexBoundariesAsync(documents, ct);

            indexed += documents.Count;
            offset += batchSize;

            _logger.LogInformation("Indexed {Indexed:N0} / {Total:N0} boundaries", indexed, totalCount);
        }

        _logger.LogInformation("Boundary indexing complete: {Count:N0} documents", indexed);
    }
}
