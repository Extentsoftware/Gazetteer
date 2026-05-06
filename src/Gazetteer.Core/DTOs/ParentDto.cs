using Gazetteer.Core.Enums;

namespace Gazetteer.Core.DTOs;

public class ParentDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public LocationType LocationType { get; set; }
}
