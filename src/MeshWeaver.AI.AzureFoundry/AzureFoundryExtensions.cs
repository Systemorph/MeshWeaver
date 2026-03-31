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
        return services.AddSingleton<IChatClientFactory, AzureClaudeChatClientAgentFactory>();
    }

    /// <summary>
    /// Adds Azure AI Foundry Claude services with configuration action.
    /// </summary>
    public static IServiceCollection AddAzureFoundryClaude(
        this IServiceCollection services,
        Action<AzureClaudeConfiguration> configure)
    {
        services.Configure(configure);
        return services.AddSingleton<IChatClientFactory, AzureClaudeChatClientAgentFactory>();
    }

    /// <summary>
    /// Adds Azure AI Foundry services.
    /// Configuration should be bound to AzureFoundryConfiguration.
    /// </summary>
    public static IServiceCollection AddAzureFoundry(this IServiceCollection services)
    {
        return services.AddSingleton<IChatClientFactory, AzureFoundryChatClientAgentFactory>();
    }

    /// <summary>
    /// Adds Azure AI Foundry services with configuration action.
    /// </summary>
    public static IServiceCollection AddAzureFoundry(
        this IServiceCollection services,
        Action<AzureFoundryConfiguration> configure)
    {
        services.Configure(configure);
        return services.AddSingleton<IChatClientFactory, AzureFoundryChatClientAgentFactory>();
    }

    /// <summary>
    /// Adds Azure AI Foundry persistent agent services.
    /// Persistent agents maintain server-side conversation history via persistent threads.
    /// </summary>
    public static IServiceCollection AddAzureFoundryPersistent(this IServiceCollection services)
        => services.AddSingleton<IChatClientFactory, AzureFoundryPersistentAgentFactory>();

    /// <summary>
    /// Adds Azure AI Foundry persistent agent services with configuration action.
    /// </summary>
    public static IServiceCollection AddAzureFoundryPersistent(
        this IServiceCollection services,
        Action<AzureFoundryPersistentConfiguration> configure)
    {
        services.Configure(configure);
        return services.AddSingleton<IChatClientFactory, AzureFoundryPersistentAgentFactory>();
    }
}
