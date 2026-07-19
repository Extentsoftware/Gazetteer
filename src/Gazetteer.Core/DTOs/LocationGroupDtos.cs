using Gazetteer.Core.Enums;

namespace Gazetteer.Core.DTOs;

public class LocationGroupSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int MemberCount { get; set; }
}

public class LocationGroupMemberDto
{
    public LocationType LocationType { get; set; }
    public int Rank { get; set; }
    public float Boost { get; set; }
}

public class LocationGroupDetailDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<LocationGroupMemberDto> Members { get; set; } = [];
}

public class UpsertLocationGroupRequest
{
    public string Name { get; set; } = string.Empty;
    public List<LocationGroupMemberDto> Members { get; set; } = [];
}
