using Gazetteer.Core.Enums;

namespace Gazetteer.Core.DTOs;

public class SearchResultDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public LocationType LocationType { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public double Lat { get; set; }
    public double Lon { get; set; }
    public bool HasGeometry { get; set; }
    public string? PostalCode { get; set; }
    public List<ParentDto> ParentChain { get; set; } = [];
    public double Score { get; set; }
}
