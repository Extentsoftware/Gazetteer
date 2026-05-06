using Gazetteer.Core.Enums;
using Gazetteer.Core.Interfaces;
using Microsoft.Extensions.Logging;
using NetTopologySuite.IO;
using System.Text.Json;

namespace Gazetteer.Seeder.Services;

public class ElasticsearchIndexer
{
    private readonly IElasticsearchService _elasticsearch;
    private readonly ILocationRepository _repository;
    private readonly ILogger<ElasticsearchIndexer> _logger;

    private static readonly HashSet<LocationType> AdminTypes =
    [
        LocationType.AdminRegion1,
        LocationType.AdminRegion2,
        LocationType.AdminRegion3
    ];

    private static readonly Dictionary<LocationType, int> AdminLevelMap = new()
    {
        [LocationType.AdminRegion1] = 4,
        [LocationType.AdminRegion2] = 6,
        [LocationType.AdminRegion3] = 8,
    };

    public ElasticsearchIndexer(
        IElasticsearchService elasticsearch,
        ILocationRepository repository,
        ILogger<ElasticsearchIndexer> logger)
    {
        _elasticsearch = elasticsearch;
        _repository = repository;
        _logger = logger;
    }

    public async Task IndexLocationsAsync(string countryCode, CancellationToken ct = default)
    {
        _logger.LogInformation("Indexing locations for {Country} to Elasticsearch...", countryCode);

        var locations = await _repository.GetByCountryAsync(countryCode, ct);
        var documents = new List<LocationIndexDocument>();

        foreach (var loc in locations)
        {
            string? parentName = null;
            if (loc.ParentId.HasValue)
            {
                var parents = await _repository.GetParentChainAsync(loc.Id, ct);
                parentName = string.Join(", ", parents.Select(p => p.Name));
            }

            documents.Add(new LocationIndexDocument
            {
                Id = loc.Id,
                Name = loc.Name,
                NameEn = loc.NameEn,
                LocalName = loc.LocalName,
                AlternateNames = loc.AlternateNames,
                LocationType = loc.LocationType.ToString(),
                CountryCode = loc.CountryCode,
                Lat = loc.Lat,
                Lon = loc.Lon,
                HasGeometry = loc.Geometry != null,
                PostalCode = loc.PostalCode,
                Population = loc.Population,
                ParentName = parentName
            });
        }

        await _elasticsearch.BulkIndexAsync(documents, ct);
        _logger.LogInformation("Indexed {Count} locations for {Country}", documents.Count, countryCode);
    }

    public async Task IndexBoundariesAsync(string countryCode, CancellationToken ct = default)
    {
        _logger.LogInformation("Indexing boundaries for {Country} to Elasticsearch...", countryCode);

        var locations = await _repository.GetLocationsWithGeometryAsync(countryCode, ct);
        var adminLocations = locations.Where(l => AdminTypes.Contains(l.LocationType)).ToList();

        var documents = new List<BoundaryIndexDocument>();
        var writer = new GeoJsonWriter();

        foreach (var loc in adminLocations)
        {
            if (loc.Geometry == null) continue;

            var geoJson = writer.Write(loc.Geometry);
            var boundary = JsonSerializer.Deserialize<object>(geoJson);

            documents.Add(new BoundaryIndexDocument
            {
                Id = loc.Id,
                OsmId = loc.OsmId,
                Name = loc.Name,
                NameEn = loc.NameEn,
                LocationType = loc.LocationType.ToString(),
                CountryCode = loc.CountryCode,
                AdminLevel = AdminLevelMap.GetValueOrDefault(loc.LocationType, 0),
                Boundary = boundary
            });
        }

        if (documents.Count > 0)
        {
            await _elasticsearch.BulkIndexBoundariesAsync(documents, ct);
            _logger.LogInformation("Indexed {Count} boundaries for {Country}", documents.Count, countryCode);
        }
    }
}
