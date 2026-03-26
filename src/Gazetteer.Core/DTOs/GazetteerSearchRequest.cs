using Gazetteer.Core.Enums;

namespace Gazetteer.Core.DTOs;

public class GazetteerSearchRequest
{
    public string Query { get; set; } = string.Empty;
    public string? CountryCode { get; set; }
    public LocationType? LocationType { get; set; }

    /// <summary>
    /// OSM ID of a parent location to scope the search within.
    /// Only results that have this location in their parent chain will be returned.
    /// </summary>
    public long? WithinOsmId { get; set; }

    public int Limit { get; set; } = 20;
}
