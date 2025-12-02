using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// Extension methods for AI services
/// </summary>
public static class AIExtensions
{
    /// <summary>
    /// Adds the AgentChatFactoryProvider that aggregates all registered IAgentChatFactory instances.
    /// Call this after registering individual factory implementations (e.g., AddAzureOpenAI, AddAzureFoundryClaude).
    /// </summary>
    public static IServiceCollection AddAgentChatFactoryProvider(this IServiceCollection services)
    {
        return services.AddSingleton<IAgentChatFactoryProvider, AgentChatFactoryProvider>();
    }
}
