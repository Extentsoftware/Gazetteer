using Gazetteer.Core.DTOs;
using Gazetteer.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using NetTopologySuite.IO;
using Newtonsoft.Json;
using System.Text.Json;

namespace Gazetteer.Infrastructure.Services;

public class SearchService : ISearchService
{
    private readonly IElasticsearchService _elasticsearch;
    private readonly ILocationRepository _repository;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public SearchService(
        IElasticsearchService elasticsearch,
        ILocationRepository repository,
        IMemoryCache cache)
    {
        _elasticsearch = elasticsearch;
        _repository = repository;
        _cache = cache;
    }

    public async Task<List<SearchResultDto>> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var cacheKey = $"search:{request.Query}:{request.CountryCode}:{request.LocationType}:{request.Limit}";

        if (_cache.TryGetValue(cacheKey, out List<SearchResultDto>? cached) && cached != null)
            return cached;

        var hits = await _elasticsearch.SearchAsync(request, ct);
        var results = new List<SearchResultDto>();

        foreach (var hit in hits)
        {
            var location = await _repository.GetByIdAsync(hit.Id, ct);
            if (location == null) continue;

            var parentChain = await _repository.GetParentChainAsync(location.Id, ct);

            results.Add(new SearchResultDto
            {
                Id = location.Id,
                Name = location.Name,
                LocationType = location.LocationType,
                CountryCode = location.CountryCode,
                Lat = location.Lat,
                Lon = location.Lon,
                HasGeometry = location.Geometry != null,
                PostalCode = location.PostalCode,
                ParentChain = parentChain.Select(p => new ParentDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    LocationType = p.LocationType
                }).ToList(),
                Score = hit.Score
            });
        }

        _cache.Set(cacheKey, results, CacheDuration);
        return results;
    }

    public async Task<LocationDetailDto?> GetLocationDetailAsync(int id, CancellationToken ct = default)
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
            Lat = location.Lat,
            Lon = location.Lon,
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

    public async Task<GeoJsonResult?> GetLocationGeometryAsync(int id, CancellationToken ct = default)
    {
        var location = await _repository.GetByIdWithGeometryAsync(id, ct);
        if (location?.Geometry == null) return null;

        var writer = new GeoJsonWriter();
        var geoJson = writer.Write(location.Geometry);
        var geometryObj = System.Text.Json.JsonSerializer.Deserialize<object>(geoJson);

        return new GeoJsonResult
        {
            Type = "Feature",
            Geometry = geometryObj,
            Properties = new Dictionary<string, object?>
            {
                ["id"] = location.Id,
                ["name"] = location.Name,
                ["locationType"] = location.LocationType.ToString(),
                ["countryCode"] = location.CountryCode
            }
        };
    }
}
