namespace Gazetteer.AI.Models;

/// <summary>
/// A ranked location candidate with confidence scoring and evidence trail.
/// </summary>
public class LocationCandidate
{
    public long Id { get; set; }
    public long OsmId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LocationType { get; set; } = string.Empty;
    public string FullAddress { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Confidence { get; set; }
    public string MatchReason { get; set; } = string.Empty;
    public List<string> SupportingClues { get; set; } = [];
}
