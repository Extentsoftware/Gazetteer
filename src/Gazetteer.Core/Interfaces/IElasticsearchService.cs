using Gazetteer.Core.Enums;

namespace Gazetteer.Core.Interfaces;

public class LocationIndexDocument
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameEn { get; set; }
    public string? LocalName { get; set; }
    public List<string> AlternateNames { get; set; } = [];
    public string LocationType { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public double Lat { get; set; }
    public double Lon { get; set; }
    public bool HasGeometry { get; set; }
    public string? PostalCode { get; set; }
    public int? Population { get; set; }
    public string? ParentName { get; set; }
}

public class LocationSearchHit
{
    public int Id { get; set; }
    public double Score { get; set; }
}

public class BoundaryIndexDocument
{
    public int Id { get; set; }
    public long OsmId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameEn { get; set; }
    public string LocationType { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public int AdminLevel { get; set; }
    public object? Boundary { get; set; }
}

public interface IElasticsearchService
{
    Task CreateIndexAsync(CancellationToken ct = default);
    Task IndexLocationAsync(LocationIndexDocument document, CancellationToken ct = default);
    Task BulkIndexAsync(IEnumerable<LocationIndexDocument> documents, CancellationToken ct = default);
    Task<List<LocationSearchHit>> SearchAsync(DTOs.SearchRequest request, CancellationToken ct = default);
    Task DeleteIndexAsync(CancellationToken ct = default);
    Task CreateBoundariesIndexAsync(CancellationToken ct = default);
    Task BulkIndexBoundariesAsync(IEnumerable<BoundaryIndexDocument> documents, CancellationToken ct = default);
    Task DeleteBoundariesIndexAsync(CancellationToken ct = default);
}
