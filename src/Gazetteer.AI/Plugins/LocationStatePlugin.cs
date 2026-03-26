using Gazetteer.AI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using System.Collections.Concurrent;
using System.ComponentModel;

namespace Gazetteer.AI.Plugins;

/// <summary>
/// Maintains a rolling location hypothesis per conversation.
/// The LLM calls this after each exchange to accumulate evidence.
/// </summary>
public class LocationStatePlugin(ILogger<LocationStatePlugin> logger)
{
    // In-memory state per conversation - keyed by a simple session identifier
    private static readonly ConcurrentDictionary<string, LocationState> States = new();

    [KernelFunction]
    [Description("Update the current location hypothesis with new clues extracted from the conversation. Call this after each caller exchange to maintain a rolling state of what we know about the location.")]
    public Task<LocationState> UpdateLocationState(
        [Description("County or region if mentioned")] string? county = null,
        [Description("Town, city, or village if mentioned")] string? town = null,
        [Description("Street or road name if mentioned")] string? street = null,
        [Description("Full or partial postcode if mentioned")] string? postcode = null,
        [Description("Landmark, business, or POI if mentioned")] string? landmark = null,
        [Description("Type of place: roadside, indoor, rural, motorway, railway, coastal, urban")] string? placeType = null,
        [Description("Caller status: stationary, moving-vehicle, moving-foot, unsure")] string? callerStatus = null,
        [Description("Direction of travel if moving")] string? directionOfTravel = null,
        [Description("New clues extracted from this exchange")] List<string>? clues = null,
        [Description("Clues that were rejected or likely misheard")] List<string>? rejectedClues = null,
        [Description("Contradictions detected between clues")] List<string>? contradictions = null,
        [Description("The single best follow-up question to narrow the location")] string? nextBestQuestion = null,
        [Description("Overall confidence 0.0 to 1.0")] double? overallConfidence = null,
        Kernel? kernel = null)
    {
        var sessionId = GetSessionId(kernel);
        var state = States.GetOrAdd(sessionId, _ => new LocationState());

        // Update fields - only overwrite if new value provided
        if (county != null) state.County = county;
        if (town != null) state.Town = town;
        if (street != null) state.Street = street;
        if (postcode != null) state.PostcodeFragment = postcode;
        if (landmark != null) state.Landmark = landmark;
        if (placeType != null) state.PlaceType = placeType;
        if (callerStatus != null) state.CallerStatus = callerStatus;
        if (directionOfTravel != null) state.DirectionOfTravel = directionOfTravel;
        if (nextBestQuestion != null) state.NextBestQuestion = nextBestQuestion;
        if (overallConfidence.HasValue) state.OverallConfidence = overallConfidence.Value;

        // Accumulate clues (don't replace)
        if (clues != null)
            state.Clues.AddRange(clues.Where(c => !state.Clues.Contains(c)));
        if (rejectedClues != null)
            state.RejectedClues.AddRange(rejectedClues.Where(c => !state.RejectedClues.Contains(c)));
        if (contradictions != null)
            state.Contradictions.AddRange(contradictions.Where(c => !state.Contradictions.Contains(c)));

        logger.LogInformation("Location state updated: confidence={Confidence}, clues={ClueCount}",
            state.OverallConfidence, state.Clues.Count);

        return Task.FromResult(state);
    }

    [KernelFunction]
    [Description("Get the current location hypothesis state showing all accumulated evidence")]
    public Task<LocationState> GetLocationState(Kernel? kernel = null)
    {
        var sessionId = GetSessionId(kernel);
        var state = States.GetOrAdd(sessionId, _ => new LocationState());
        return Task.FromResult(state);
    }

    private static string GetSessionId(Kernel? kernel)
    {
        if (kernel?.Data.TryGetValue("SessionId", out var sessionId) == true && sessionId is string id)
            return id;
        return "default";
    }
}
