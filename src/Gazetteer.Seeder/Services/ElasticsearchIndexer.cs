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

        await IndexLocationsAsync(esService, db, parentLookup, batchSize, ct);
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

    private async Task IndexLocationsAsync(
        IElasticsearchService esService, GazetteerDbContext db,
        Dictionary<long, ParentLookupEntry> parentLookup,
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
                return new LocationIndexDocument
                {
                    Id = l.Id,
                    OsmId = l.OsmId,
                    Name = l.Name,
                    NameEn = l.NameEn,
                    AlternateNames = l.AlternateNames,
                    LocationType = l.LocationType,
                    CountryCode = l.CountryCode,
                    PostalCode = l.PostalCode,
                    Latitude = l.Latitude,
                    Longitude = l.Longitude,
                    Population = l.Population,
                    HasGeometry = l.HasGeometry,
                    ParentChain = BuildParentChain(parents),
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
