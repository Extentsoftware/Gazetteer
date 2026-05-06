using Gazetteer.Core.Enums;

namespace Gazetteer.Core.DTOs;

public class LocationDetailDto
{
    public int Id { get; set; }
    public long OsmId { get; set; }
    public OsmType OsmType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LocalName { get; set; }
    public string? NameEn { get; set; }
    public List<string> AlternateNames { get; set; } = [];
    public LocationType LocationType { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public double Lat { get; set; }
    public double Lon { get; set; }
    public bool HasGeometry { get; set; }
    public int? Population { get; set; }
    public string? PostalCode { get; set; }
    public List<ParentDto> ParentChain { get; set; } = [];
}
