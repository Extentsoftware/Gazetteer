using Gazetteer.AI.Configuration;
using Gazetteer.AI.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Gazetteer.AI.Agent;

public class KernelFactory(
    OpenAIConfig config,
    IServiceProvider serviceProvider,
    ILogger<KernelFactory> logger)
{
    private Kernel? _kernel;

    public Task<Kernel> CreateKernelAsync(CancellationToken ct = default)
    {
        if (_kernel != null)
        {
            logger.LogInformation("Cloning kernel");
            return Task.FromResult(_kernel.Clone());
        }

        logger.LogInformation("Creating new kernel with model {Model}", config.ModelId);

        var builder = Kernel.CreateBuilder();
        builder.Services.AddLogging(l => l.SetMinimumLevel(LogLevel.Information));
        builder.Services.AddOpenAIChatCompletion(
            modelId: config.ModelId,
            apiKey: config.ApiKey,
            orgId: config.OrgId,
            serviceId: "openai");

        // Register tool invocation filter
        var toolFilter = serviceProvider.GetRequiredService<ToolInvocationFilter>();
        builder.Services.AddSingleton<IAutoFunctionInvocationFilter>(toolFilter);

        var kernel = builder.Build();

        // Register plugins
        kernel.Plugins.AddFromObject(serviceProvider.GetRequiredService<GazetteerSearchPlugin>(), "Gazetteer");
        kernel.Plugins.AddFromObject(serviceProvider.GetRequiredService<LocationStatePlugin>(), "LocationState");
        kernel.Plugins.AddFromObject(serviceProvider.GetRequiredService<UiCommandPlugin>(), "UI");

        logger.LogInformation("Kernel created with {Count} plugins", kernel.Plugins.Count);
        _kernel = kernel;
        return Task.FromResult(kernel);
    }
}
