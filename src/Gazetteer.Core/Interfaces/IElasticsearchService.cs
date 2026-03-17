using Gazetteer.Core.DTOs;

namespace Gazetteer.Core.Interfaces;

public interface IElasticsearchService
{
    Task CreateIndexAsync(CancellationToken ct = default);
    Task IndexLocationAsync(LocationIndexDocument document, CancellationToken ct = default);
    Task BulkIndexAsync(IEnumerable<LocationIndexDocument> documents, CancellationToken ct = default);
    Task<List<LocationSearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default);
    Task DeleteIndexAsync(CancellationToken ct = default);
}

public class LocationIndexDocument
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameEn { get; set; }
    public string? AlternateNames { get; set; }
    public string LocationType { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string? PostalCode { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public long? Population { get; set; }
    public string? ParentChain { get; set; }
    public bool HasGeometry { get; set; }
}

public class LocationSearchHit
{
    public long Id { get; set; }
    public double Score { get; set; }
}
