namespace Gazetteer.AI.Models;

/// <summary>
/// Rolling location hypothesis maintained across conversation turns.
/// Accumulates evidence from each exchange to progressively narrow the location.
/// </summary>
public class LocationState
{
    public string? County { get; set; }
    public string? Town { get; set; }
    public string? Street { get; set; }
    public string? PostcodeFragment { get; set; }
    public string? Landmark { get; set; }
    public string? PlaceType { get; set; }
    public string? CallerStatus { get; set; }
    public string? DirectionOfTravel { get; set; }
    public List<string> Clues { get; set; } = [];
    public List<string> RejectedClues { get; set; } = [];
    public List<LocationCandidate> Candidates { get; set; } = [];
    public string? NextBestQuestion { get; set; }
    public double OverallConfidence { get; set; }
    public List<string> Contradictions { get; set; } = [];
}
