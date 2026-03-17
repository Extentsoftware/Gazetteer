using Gazetteer.Core.Enums;

namespace Gazetteer.Core.DTOs;

public class SearchResultDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public LocationType LocationType { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public bool HasGeometry { get; set; }
    public string? PostalCode { get; set; }
    public List<ParentDto> ParentChain { get; set; } = new();
    public double Score { get; set; }
}
