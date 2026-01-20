using MeshWeaver.AI.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// Extension methods for AI services
/// </summary>
public static class AIExtensions
{
    /// <summary>
    /// Adds the AI chat services including persistence.
    /// Call this after registering individual factory implementations (e.g., AddAzureOpenAI, AddAzureFoundryClaude).
    /// </summary>
    public static IServiceCollection AddAgentChatServices(this IServiceCollection services)
    {
        // Ensure ChatPersistenceService is registered
        services.AddMemoryChatPersistence();

        return services;
    }

    /// <summary>
    /// Backwards-compatible method - same as AddAgentChatServices.
    /// </summary>
    [Obsolete("Use AddAgentChatServices instead")]
    public static IServiceCollection AddAgentChatFactoryProvider(this IServiceCollection services)
    {
        return services.AddAgentChatServices();
    }
}
