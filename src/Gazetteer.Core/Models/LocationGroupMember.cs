using Gazetteer.Core.Enums;

namespace Gazetteer.Core.Models;

public class LocationGroupMember
{
    public Guid GroupId { get; set; }
    public LocationGroup Group { get; set; } = null!;
    public LocationType LocationType { get; set; }

    /// <summary>1 = most important within the group.</summary>
    public int Rank { get; set; }

    /// <summary>Elasticsearch score boost for this location type.</summary>
    public float Boost { get; set; }
}
