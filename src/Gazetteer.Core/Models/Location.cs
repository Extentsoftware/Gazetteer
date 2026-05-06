using Gazetteer.Core.Enums;
using NetTopologySuite.Geometries;

namespace Gazetteer.Core.Models;

public class Location
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
    public Geometry? Geometry { get; set; }
    public int? Population { get; set; }
    public string? PostalCode { get; set; }
    public int? ParentId { get; set; }
    public Location? Parent { get; set; }
    public List<Location> Children { get; set; } = [];
}
