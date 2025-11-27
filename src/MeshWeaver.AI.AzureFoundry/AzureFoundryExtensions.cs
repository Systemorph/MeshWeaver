using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Extension methods for adding Azure AI Foundry services
/// </summary>
public static class AzureFoundryExtensions
{
    /// <summary>
    /// Adds Azure AI Foundry Claude services.
    /// Configuration should be bound to AzureClaudeConfiguration.
    /// </summary>
    public static IServiceCollection AddAzureFoundryClaude(this IServiceCollection services)
    {
        return services.AddSingleton<IAgentChatFactory, AzureClaudeChatCompletionAgentChatFactory>();
    }

    /// <summary>
    /// Adds Azure AI Foundry Claude services with configuration action.
    /// </summary>
    public static IServiceCollection AddAzureFoundryClaude(
        this IServiceCollection services,
        Action<AzureClaudeConfiguration> configure)
    {
        services.Configure(configure);
        return services.AddSingleton<IAgentChatFactory, AzureClaudeChatCompletionAgentChatFactory>();
    }

    /// <summary>
    /// Adds Azure AI Foundry services.
    /// Configuration should be bound to AzureFoundryConfiguration.
    /// </summary>
    public static IServiceCollection AddAzureFoundry(this IServiceCollection services)
    {
        return services.AddSingleton<IAgentChatFactory, AzureFoundryChatCompletionAgentChatFactory>();
    }

    /// <summary>
    /// Adds Azure AI Foundry services with configuration action.
    /// </summary>
    public static IServiceCollection AddAzureFoundry(
        this IServiceCollection services,
        Action<AzureFoundryConfiguration> configure)
    {
        services.Configure(configure);
        return services.AddSingleton<IAgentChatFactory, AzureFoundryChatCompletionAgentChatFactory>();
    }
}
