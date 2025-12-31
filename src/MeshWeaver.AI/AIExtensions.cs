using MeshWeaver.AI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// Extension methods for AI services
/// </summary>
public static class AIExtensions
{
    /// <summary>
    /// Adds the AgentResolver service for hierarchical agent resolution from the graph.
    /// </summary>
    public static IServiceCollection AddAgentResolver(this IServiceCollection services)
    {
        return services.AddSingleton<IAgentResolver, AgentResolver>();
    }

    /// <summary>
    /// Adds the AgentChatFactoryProvider that aggregates all registered IAgentChatFactory instances.
    /// Call this after registering individual factory implementations (e.g., AddAzureOpenAI, AddAzureFoundryClaude).
    /// Also registers the AgentResolver if not already registered.
    /// </summary>
    public static IServiceCollection AddAgentChatFactoryProvider(this IServiceCollection services)
    {
        // Ensure AgentResolver is registered
        services.AddAgentResolver();

        return services.AddSingleton<IAgentChatFactoryProvider, AgentChatFactoryProvider>();
    }
}
