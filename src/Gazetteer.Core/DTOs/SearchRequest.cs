using Gazetteer.Core.Enums;

namespace Gazetteer.Core.DTOs;

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public string? CountryCode { get; set; }
    public LocationType? LocationType { get; set; }
    public int Limit { get; set; } = 20;
}
