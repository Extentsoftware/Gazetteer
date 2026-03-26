using Microsoft.AspNetCore.SignalR.Client;
using System.Text.Json;

namespace Gazetteer.Web.Services;

/// <summary>
/// SignalR client for the Location Copilot chat hub.
/// </summary>
public class CopilotChatService : IAsyncDisposable
{
    private HubConnection? _connection;

    public event Action<string>? OnTextReceived;
    public event Action<string>? OnProgressReceived;
    public event Action<string>? OnCandidatesReceived;
    public event Action<string>? OnEvidenceReceived;
    public event Action<string>? OnSuggestionsReceived;
    public event Action<string>? OnMapReceived;
    public event Action? OnStreamComplete;
    public event Action<string>? OnError;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string hubUrl)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)])
            .Build();

        await _connection.StartAsync();
    }

    public async Task SendMessageAsync(string message, string? sessionId = null)
    {
        if (_connection == null) return;

        try
        {
            var stream = _connection.StreamAsync<ChatHubResponse>("StreamChat", message, sessionId);

            await foreach (var response in stream)
            {
                switch (response.ContentType)
                {
                    case "text":
                        OnTextReceived?.Invoke(response.Content);
                        break;
                    case "progress":
                        OnProgressReceived?.Invoke(response.Content);
                        break;
                    case "candidates":
                        OnCandidatesReceived?.Invoke(response.Content);
                        break;
                    case "evidence":
                        OnEvidenceReceived?.Invoke(response.Content);
                        break;
                    case "suggestions":
                        OnSuggestionsReceived?.Invoke(response.Content);
                        break;
                    case "map":
                        OnMapReceived?.Invoke(response.Content);
                        break;
                }
            }

            OnStreamComplete?.Invoke();
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex.Message);
        }
    }

    public async Task ClearSessionAsync(string? sessionId = null)
    {
        if (_connection != null)
            await _connection.InvokeAsync("ClearSession", sessionId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
            await _connection.DisposeAsync();
    }
}

public class ChatHubResponse
{
    public string ContentType { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
