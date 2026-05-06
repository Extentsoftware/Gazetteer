using System.Text.Json.Serialization;

namespace Gazetteer.Core.DTOs;

public class GeoJsonResult
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Feature";

    [JsonPropertyName("geometry")]
    public object? Geometry { get; set; }

    [JsonPropertyName("properties")]
    public Dictionary<string, object?> Properties { get; set; } = [];
}
