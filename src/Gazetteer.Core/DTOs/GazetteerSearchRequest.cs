using Gazetteer.Core.Enums;

namespace Gazetteer.Core.DTOs;

public class GazetteerSearchRequest
{
    public string Query { get; set; } = string.Empty;
    public string? CountryCode { get; set; }
    public LocationType? LocationType { get; set; }

    /// <summary>
    /// When set, search is scoped to this group's location types and uses their boosts.
    /// Takes precedence over <see cref="LocationType"/>.
    /// </summary>
    public Guid? LocationGroupId { get; set; }

    /// <summary>
    /// Resolved type filters/boosts (populated by SearchService when a group is selected,
    /// or derived from a single LocationType). Not typically set by API callers.
    /// </summary>
    public List<LocationTypeBoost>? TypeBoosts { get; set; }

    /// <summary>
    /// OSM ID of a parent location to scope the search within.
    /// Only results that have this location in their parent chain will be returned.
    /// </summary>
    public long? WithinOsmId { get; set; }

    public int Limit { get; set; } = 20;
}

public class LocationTypeBoost
{
    public LocationType LocationType { get; set; }
    public float Boost { get; set; }
}
