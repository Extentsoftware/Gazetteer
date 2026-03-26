using Gazetteer.AI.StreamContent;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using System.Text.Json;
using System.Threading.Channels;

namespace Gazetteer.AI.Agent;

/// <summary>
/// Intercepts tool invocations to stream progress and structured results to the UI.
/// </summary>
public class ToolInvocationFilter(ILogger<ToolInvocationFilter> logger) : IAutoFunctionInvocationFilter
{
    public const string ChannelWriterKey = "ChannelWriter";
    public const string ConversationIdKey = "ConversationId";

    public async Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context,
        Func<AutoFunctionInvocationContext, Task> next)
    {
        var functionName = $"{context.Function.PluginName}-{context.Function.Name}";
        logger.LogInformation("Tool invocation: {Function}", functionName);

        // Send progress to UI
        ChannelWriter<ChatStreamContent>? writer = null;
        if (context.Kernel.Data.TryGetValue(ChannelWriterKey, out var writerObj) &&
            writerObj is ChannelWriter<ChatStreamContent> w)
        {
            writer = w;
            var progressText = context.Function.Description ?? functionName;
            await writer.WriteAsync(new ProgressStreamContent(progressText));
        }

        try
        {
            await next(context);

            // Stream structured results from UI command plugin
            var result = context.Result;
            if (result?.GetValue<object>() is ChatStreamContent streamContent && writer != null)
            {
                await writer.WriteAsync(streamContent);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tool invocation failed: {Function}", functionName);
            context.Result = new FunctionResult(context.Function, $"Error: {ex.Message}");
        }
    }
}
