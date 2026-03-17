namespace Gazetteer.Core.DTOs;

public class GeoJsonResult
{
    public string Type { get; set; } = "Feature";
    public object? Geometry { get; set; }
    public object? Properties { get; set; }
}
