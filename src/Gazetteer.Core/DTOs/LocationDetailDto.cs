using Gazetteer.Core.Enums;

namespace Gazetteer.Core.DTOs;

public class LocationDetailDto
{
    public long Id { get; set; }
    public long OsmId { get; set; }
    public OsmType OsmType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LocalName { get; set; }
    public string? NameEn { get; set; }
    public string? AlternateNames { get; set; }
    public LocationType LocationType { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public bool HasGeometry { get; set; }
    public long? Population { get; set; }
    public string? PostalCode { get; set; }
    public List<ParentDto> ParentChain { get; set; } = [];
}
