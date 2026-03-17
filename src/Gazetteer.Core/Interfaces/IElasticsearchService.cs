using Gazetteer.Core.DTOs;

namespace Gazetteer.Core.Interfaces;

public interface IElasticsearchService
{
    Task CreateIndexAsync(CancellationToken ct = default);
    Task IndexLocationAsync(LocationIndexDocument document, CancellationToken ct = default);
    Task BulkIndexAsync(IEnumerable<LocationIndexDocument> documents, CancellationToken ct = default);
    Task<List<LocationSearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default);
    Task DeleteIndexAsync(CancellationToken ct = default);

    // Boundaries index
    Task CreateBoundariesIndexAsync(CancellationToken ct = default);
    Task BulkIndexBoundariesAsync(IEnumerable<BoundaryIndexDocument> documents, CancellationToken ct = default);
    Task DeleteBoundariesIndexAsync(CancellationToken ct = default);
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

public class BoundaryIndexDocument
{
    public long Id { get; set; }
    public long OsmId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameEn { get; set; }
    public string LocationType { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public int AdminLevel { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public long? Population { get; set; }
    public string? ParentChain { get; set; }
    /// <summary>
    /// GeoJSON geometry object serialized as a dictionary for Elasticsearch geo_shape mapping.
    /// </summary>
    public object Boundary { get; set; } = null!;
}

public class LocationSearchHit
{
    public long Id { get; set; }
    public double Score { get; set; }
}
