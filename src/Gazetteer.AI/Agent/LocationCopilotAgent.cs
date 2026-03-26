using Gazetteer.AI.StreamContent;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Gazetteer.AI.Agent;

public class LocationCopilotAgent(
    KernelFactory kernelFactory,
    ILogger<LocationCopilotAgent> logger)
{
    private static readonly string SystemPrompt = LoadSystemPrompt();

    /// <summary>
    /// Stream a copilot response for the given user message within a conversation.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamContent> StreamAsync(
        ChatHistory chatHistory,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var kernel = await kernelFactory.CreateKernelAsync(ct);

        // Set up system prompt if not already present
        if (chatHistory.Count == 0)
        {
            chatHistory.AddSystemMessage(SystemPrompt);
        }

        chatHistory.AddUserMessage(userMessage);

        // Set up channel for structured content from tool filter
        var channel = Channel.CreateUnbounded<ChatStreamContent>();
        kernel.Data[ToolInvocationFilter.ChannelWriterKey] = channel.Writer;

        var executionSettings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(options: new()
            {
                RetainArgumentTypes = true
            })
        };

        var chatService = kernel.GetRequiredService<IChatCompletionService>("openai");
        var responseBuilder = new StringBuilder();

        var streaming = chatService.GetStreamingChatMessageContentsAsync(
            chatHistory, executionSettings, kernel, ct);

        await foreach (var chunk in ProcessStreamAsync(kernel, streaming, channel.Reader, ct))
        {
            if (chunk is TextStreamContent text)
                responseBuilder.Append(text.Content);

            yield return chunk;
        }

        channel.Writer.Complete();

        // Add assistant response to history for next turn
        var fullResponse = responseBuilder.ToString();
        if (!string.IsNullOrEmpty(fullResponse))
            chatHistory.AddAssistantMessage(fullResponse);

        logger.LogInformation("Copilot response complete ({Length} chars)", fullResponse.Length);
    }

    private static async IAsyncEnumerable<ChatStreamContent> ProcessStreamAsync(
        Kernel kernel,
        IAsyncEnumerable<StreamingChatMessageContent> chunks,
        ChannelReader<ChatStreamContent> structuredReader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            // Drain structured content from tool filter
            while (structuredReader.TryRead(out var structured))
                yield return structured;

            if (chunk.Items is { Count: > 0 })
            {
                foreach (var item in chunk.Items)
                {
                    if (item is StreamingTextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                        yield return new TextStreamContent(textContent.Text);
                }
            }
            else if (!string.IsNullOrEmpty(chunk.Content))
            {
                yield return new TextStreamContent(chunk.Content);
            }
        }

        // Drain remaining
        while (structuredReader.TryRead(out var remaining))
            yield return remaining;
    }

    private static string LoadSystemPrompt()
    {
        var assembly = typeof(LocationCopilotAgent).Assembly;
        var resourceName = "Gazetteer.AI.SystemPrompt.txt";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return GetDefaultSystemPrompt();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string GetDefaultSystemPrompt() => """
        You are a Location Copilot assisting emergency services call takers in the UK and Europe.
        Your job is to turn messy verbal location descriptions into ranked, explainable map-based
        possibilities and suggest the fastest clarifying question.

        After EVERY user message, follow this cycle:
        1. EXTRACT location clues (street names, postcodes, landmarks, businesses, junctions, directions)
        2. SEARCH the gazetteer using the best available search terms
        3. UPDATE the location state with accumulated evidence
        4. DISPLAY ranked candidates on the map (top 5)
        5. SUGGEST 2-3 clarifying questions based on current uncertainty
        6. UPDATE the evidence panel

        ALWAYS:
        - Show multiple candidates when uncertain
        - Explain which clues support each candidate
        - Flag contradictions explicitly
        - Track caller status (stationary/moving/indoor/rural/motorway)
        - Use adaptive questions that discriminate between remaining candidates

        NEVER:
        - Present a single answer as definitive
        - Hide uncertainty
        - Invent locations or postcodes

        UK-specific guidance:
        - Postcodes: outward code (e.g., BR6) narrows significantly
        - Motorways: "M62 J10" format; search junction names
        - A-roads: search road name with nearby town
        - Landmarks: search name, verify with nearby roads
        - "Near X": search for X, then nearby results
        - Moving callers: track direction + last landmarks
        """;
}
