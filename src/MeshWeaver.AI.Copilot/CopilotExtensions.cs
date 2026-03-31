using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Copilot;

/// <summary>
/// Extension methods for adding GitHub Copilot services.
/// </summary>
public static class CopilotExtensions
{
    /// <summary>
    /// Adds GitHub Copilot services to the service collection.
    /// Configuration should be bound to CopilotConfiguration.
    /// </summary>
    public static IServiceCollection AddCopilot(this IServiceCollection services)
    {
        return services.AddSingleton<IChatClientFactory, CopilotChatClientAgentFactory>();
    }

    /// <summary>
    /// Adds GitHub Copilot services with configuration action.
    /// </summary>
    public static IServiceCollection AddCopilot(
        this IServiceCollection services,
        Action<CopilotConfiguration> configure)
    {
        services.Configure(configure);
        return services.AddSingleton<IChatClientFactory, CopilotChatClientAgentFactory>();
    }
}
