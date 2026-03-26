namespace Gazetteer.AI.StreamContent;

/// <summary>
/// Base class for all streamed content from the copilot agent.
/// </summary>
public abstract class ChatStreamContent
{
    public abstract string ContentType { get; }
}

public class TextStreamContent(string content) : ChatStreamContent
{
    public override string ContentType => "text";
    public string Content { get; } = content;
}

public class ProgressStreamContent(string message) : ChatStreamContent
{
    public override string ContentType => "progress";
    public string Message { get; } = message;
}

public class CandidatesStreamContent(List<Models.LocationCandidate> candidates) : ChatStreamContent
{
    public override string ContentType => "candidates";
    public List<Models.LocationCandidate> Candidates { get; } = candidates;
}

public class EvidenceStreamContent(Models.LocationState state) : ChatStreamContent
{
    public override string ContentType => "evidence";
    public Models.LocationState State { get; } = state;
}

public class SuggestionsStreamContent(List<string> suggestions) : ChatStreamContent
{
    public override string ContentType => "suggestions";
    public List<string> Suggestions { get; } = suggestions;
}

public class MapStreamContent(List<Models.LocationCandidate> markers) : ChatStreamContent
{
    public override string ContentType => "map";
    public List<Models.LocationCandidate> Markers { get; } = markers;
}
