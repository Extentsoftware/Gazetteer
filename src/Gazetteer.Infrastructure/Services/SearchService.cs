using Gazetteer.Core.DTOs;
using Gazetteer.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NetTopologySuite.IO;

namespace Gazetteer.Infrastructure.Services;

public class SearchService : ISearchService
{
    private readonly IElasticsearchService _elasticsearch;
    private readonly ILocationRepository _repository;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SearchService> _logger;

    public SearchService(
        IElasticsearchService elasticsearch,
        ILocationRepository repository,
        IMemoryCache cache,
        ILogger<SearchService> logger)
    {
        _elasticsearch = elasticsearch;
        _repository = repository;
        _cache = cache;
        _logger = logger;
    }

    public async Task<List<SearchResultDto>> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length < 2)
            return new List<SearchResultDto>();

        var cacheKey = $"search:{request.Query}:{request.CountryCode}:{request.LocationType}:{request.Limit}";
        if (_cache.TryGetValue(cacheKey, out List<SearchResultDto>? cached) && cached != null)
            return cached;

        var hits = await _elasticsearch.SearchAsync(request, ct);
        if (hits.Count == 0)
            return new List<SearchResultDto>();

        var results = new List<SearchResultDto>();
        foreach (var hit in hits)
        {
            var location = await _repository.GetByIdAsync(hit.Id, ct);
            if (location == null) continue;

            var parentChain = await _repository.GetParentChainAsync(hit.Id, ct);

            results.Add(new SearchResultDto
            {
                Id = location.Id,
                Name = location.Name,
                LocationType = location.LocationType,
                CountryCode = location.CountryCode,
                Latitude = location.Latitude,
                Longitude = location.Longitude,
                HasGeometry = location.Geometry != null,
                PostalCode = location.PostalCode,
                Score = hit.Score,
                ParentChain = parentChain.Select(p => new ParentDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    LocationType = p.LocationType
                }).ToList()
            });
        }

        _cache.Set(cacheKey, results, TimeSpan.FromSeconds(30));
        return results;
    }

    public async Task<LocationDetailDto?> GetLocationDetailAsync(long id, CancellationToken ct = default)
    {
        var location = await _repository.GetByIdAsync(id, ct);
        if (location == null) return null;

        var parentChain = await _repository.GetParentChainAsync(id, ct);

        return new LocationDetailDto
        {
            Id = location.Id,
            OsmId = location.OsmId,
            OsmType = location.OsmType,
            Name = location.Name,
            LocalName = location.LocalName,
            NameEn = location.NameEn,
            AlternateNames = location.AlternateNames,
            LocationType = location.LocationType,
            CountryCode = location.CountryCode,
            Latitude = location.Latitude,
            Longitude = location.Longitude,
            HasGeometry = location.Geometry != null,
            Population = location.Population,
            PostalCode = location.PostalCode,
            ParentChain = parentChain.Select(p => new ParentDto
            {
                Id = p.Id,
                Name = p.Name,
                LocationType = p.LocationType
            }).ToList()
        };
    }

    public async Task<GeoJsonResult?> GetLocationGeometryAsync(long id, CancellationToken ct = default)
    {
        var location = await _repository.GetByIdWithGeometryAsync(id, ct);
        if (location == null) return null;

        var geometry = location.Geometry;
        if (geometry == null)
        {
            // Return a point if no polygon geometry exists
            return new GeoJsonResult
            {
                Type = "Feature",
                Geometry = new
                {
                    type = "Point",
                    coordinates = new[] { location.Longitude, location.Latitude }
                },
                Properties = new
                {
                    id = location.Id,
                    name = location.Name,
                    locationType = location.LocationType.ToString()
                }
            };
        }

        var writer = new GeoJsonWriter();
        var geoJson = writer.Write(geometry);

        return new GeoJsonResult
        {
            Type = "Feature",
            Geometry = System.Text.Json.JsonSerializer.Deserialize<object>(geoJson),
            Properties = new
            {
                id = location.Id,
                name = location.Name,
                locationType = location.LocationType.ToString()
            }
        };
    }
}
