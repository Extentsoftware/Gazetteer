namespace Gazetteer.Core.Models;

public class LocationGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public ICollection<LocationGroupMember> Members { get; set; } = [];
}
