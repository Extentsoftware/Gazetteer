using Gazetteer.AI.Agent;
using Gazetteer.AI.Configuration;
using Gazetteer.AI.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gazetteer.AI.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGazetteerAI(this IServiceCollection services, IConfiguration configuration)
    {
        // Configuration
        var openAiConfig = new OpenAIConfig();
        configuration.GetSection("OpenAI").Bind(openAiConfig);
        services.AddSingleton(openAiConfig);

        // Agent infrastructure
        services.AddSingleton<KernelFactory>();
        services.AddSingleton<ToolInvocationFilter>();
        services.AddSingleton<LocationCopilotAgent>();

        // Plugins
        services.AddSingleton<GazetteerSearchPlugin>();
        services.AddSingleton<LocationStatePlugin>();
        services.AddSingleton<UiCommandPlugin>();

        return services;
    }
}
