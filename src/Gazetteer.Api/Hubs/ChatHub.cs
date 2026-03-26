using Gazetteer.AI.Agent;
using Gazetteer.AI.StreamContent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Gazetteer.Api.Hubs;

public class ChatHubResponse
{
    public string ContentType { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public class ChatHub(LocationCopilotAgent agent) : Hub
{
    // In-memory chat histories per connection - simple for MVP
    private static readonly Dictionary<string, ChatHistory> ChatHistories = new();

    public async IAsyncEnumerable<ChatHubResponse> StreamChat(
        string message,
        string? sessionId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        sessionId ??= Context.ConnectionId;

        if (!ChatHistories.TryGetValue(sessionId, out var history))
        {
            history = new ChatHistory();
            ChatHistories[sessionId] = history;
        }

        await foreach (var chunk in agent.StreamAsync(history, message, ct))
        {
            yield return chunk switch
            {
                TextStreamContent text => new ChatHubResponse
                {
                    ContentType = "text",
                    Content = text.Content
                },
                ProgressStreamContent progress => new ChatHubResponse
                {
                    ContentType = "progress",
                    Content = progress.Message
                },
                CandidatesStreamContent candidates => new ChatHubResponse
                {
                    ContentType = "candidates",
                    Content = JsonSerializer.Serialize(candidates.Candidates)
                },
                EvidenceStreamContent evidence => new ChatHubResponse
                {
                    ContentType = "evidence",
                    Content = JsonSerializer.Serialize(evidence.State)
                },
                SuggestionsStreamContent suggestions => new ChatHubResponse
                {
                    ContentType = "suggestions",
                    Content = JsonSerializer.Serialize(suggestions.Suggestions)
                },
                MapStreamContent map => new ChatHubResponse
                {
                    ContentType = "map",
                    Content = JsonSerializer.Serialize(map.Markers)
                },
                _ => new ChatHubResponse
                {
                    ContentType = "unknown",
                    Content = chunk.ContentType
                }
            };
        }
    }

    public Task ClearSession(string? sessionId = null)
    {
        sessionId ??= Context.ConnectionId;
        ChatHistories.Remove(sessionId);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        ChatHistories.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
