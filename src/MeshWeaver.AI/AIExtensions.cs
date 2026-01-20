using MeshWeaver.AI.Persistence;
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
    /// Also registers the ChatPersistenceService if not already registered.
    /// </summary>
    public static IServiceCollection AddAgentChatFactoryProvider(this IServiceCollection services)
    {
        // Ensure ChatPersistenceService is registered
        services.AddMemoryChatPersistence();

        return services.AddSingleton<IAgentChatFactoryProvider, AgentChatFactoryProvider>();
    }
}
