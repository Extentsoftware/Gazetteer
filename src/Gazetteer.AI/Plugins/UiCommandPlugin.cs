using Gazetteer.AI.Models;
using Gazetteer.AI.StreamContent;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Threading.Channels;

namespace Gazetteer.AI.Plugins;

/// <summary>
/// Sends structured UI commands to the Blazor client via the streaming channel.
/// The LLM calls these to update the map, evidence panel, and suggestions bar.
/// </summary>
public class UiCommandPlugin(ILogger<UiCommandPlugin> logger)
{
    [KernelFunction]
    [Description("Display ranked location candidates on the map with numbered markers. Call this after searching to show results visually.")]
    public async Task ShowCandidatesOnMap(
        [Description("List of location candidates to display as map markers")] List<LocationCandidate> candidates,
        Kernel? kernel = null)
    {
        logger.LogInformation("Showing {Count} candidates on map", candidates.Count);
        var writer = GetWriter(kernel);
        if (writer != null)
        {
            await writer.WriteAsync(new MapStreamContent(candidates));
            await writer.WriteAsync(new CandidatesStreamContent(candidates));
        }
    }

    [KernelFunction]
    [Description("Send suggested follow-up questions for the call taker to click. Questions should be adaptive based on current uncertainty and help discriminate between remaining candidates.")]
    public async Task ShowSuggestedQuestions(
        [Description("2-3 suggested questions for the call taker")] List<string> questions,
        Kernel? kernel = null)
    {
        logger.LogInformation("Showing {Count} suggested questions", questions.Count);
        var writer = GetWriter(kernel);
        if (writer != null)
            await writer.WriteAsync(new SuggestionsStreamContent(questions));
    }

    [KernelFunction]
    [Description("Update the evidence/clue panel with the current location hypothesis. Shows all accumulated clues, confidence, contradictions, and current best guess.")]
    public async Task UpdateEvidencePanel(
        [Description("Current location state with all evidence")] LocationState state,
        Kernel? kernel = null)
    {
        logger.LogInformation("Updating evidence panel (confidence={Confidence})", state.OverallConfidence);
        var writer = GetWriter(kernel);
        if (writer != null)
            await writer.WriteAsync(new EvidenceStreamContent(state));
    }

    private static ChannelWriter<ChatStreamContent>? GetWriter(Kernel? kernel)
    {
        if (kernel?.Data.TryGetValue(Agent.ToolInvocationFilter.ChannelWriterKey, out var obj) == true &&
            obj is ChannelWriter<ChatStreamContent> writer)
            return writer;
        return null;
    }
}
