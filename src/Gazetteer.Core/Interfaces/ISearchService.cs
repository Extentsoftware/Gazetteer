using Gazetteer.Core.DTOs;

namespace Gazetteer.Core.Interfaces;

public interface ISearchService
{
    Task<List<SearchResultDto>> SearchAsync(SearchRequest request, CancellationToken ct = default);
    Task<LocationDetailDto?> GetLocationDetailAsync(long id, CancellationToken ct = default);
    Task<GeoJsonResult?> GetLocationGeometryAsync(long id, CancellationToken ct = default);
}
