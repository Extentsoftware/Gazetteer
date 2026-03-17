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
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ElasticsearchIndexer> _logger;

    private static readonly HashSet<LocationType> AdminTypes = new()
    {
        LocationType.Country,
        LocationType.AdminRegion1,
        LocationType.AdminRegion2,
        LocationType.AdminRegion3
    };

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

        await IndexLocationsAsync(esService, db, batchSize, ct);
        await IndexBoundariesAsync(esService, db, batchSize, ct);

        _logger.LogInformation("Elasticsearch indexing complete");
    }

    private async Task IndexLocationsAsync(
        IElasticsearchService esService, GazetteerDbContext db,
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
                    ParentName = l.Parent != null ? l.Parent.Name : null,
                    GrandParentName = l.Parent != null && l.Parent.Parent != null ? l.Parent.Parent.Name : null
                })
                .ToListAsync(ct);

            var documents = locations.Select(l => new LocationIndexDocument
            {
                Id = l.Id,
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
                ParentChain = BuildParentChain(l.ParentName, l.GrandParentName)
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
                    ParentName = l.Parent != null ? l.Parent.Name : null,
                    GrandParentName = l.Parent != null && l.Parent.Parent != null ? l.Parent.Parent.Name : null
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
                    ParentChain = BuildParentChain(l.ParentName, l.GrandParentName),
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

    private static string? BuildParentChain(string? parent, string? grandParent)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(parent)) parts.Add(parent);
        if (!string.IsNullOrEmpty(grandParent)) parts.Add(grandParent);
        return parts.Count > 0 ? string.Join(" > ", parts) : null;
    }
}
