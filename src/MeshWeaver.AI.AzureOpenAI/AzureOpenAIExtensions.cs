using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.AzureOpenAI;

/// <summary>
/// Extension methods for adding Azure OpenAI services
/// </summary>
public static class AzureOpenAIExtensions
{
    /// <summary>
    /// Adds Azure OpenAI services to the service collection
    /// </summary>
    public static IServiceCollection AddAzureOpenAI(this IServiceCollection services)
    {
        return services.AddSingleton<IAgentChatFactory, AzureOpenAIChatCompletionAgentChatFactory>();
    }

    /// <summary>
    /// Adds Azure OpenAI services with configuration action.
    /// </summary>
    public static IServiceCollection AddAzureOpenAI(
        this IServiceCollection services,
        Action<AzureOpenAIConfiguration> configure)
    {
        services.Configure(configure);
        return services.AddSingleton<IAgentChatFactory, AzureOpenAIChatCompletionAgentChatFactory>();
    }
}
