using Gazetteer.Core.DTOs;

namespace Gazetteer.Core.Interfaces;

public interface ISearchService
{
    Task<List<SearchResultDto>> SearchAsync(SearchRequest request, CancellationToken ct = default);
    Task<LocationDetailDto?> GetLocationDetailAsync(int id, CancellationToken ct = default);
    Task<GeoJsonResult?> GetLocationGeometryAsync(int id, CancellationToken ct = default);
}
