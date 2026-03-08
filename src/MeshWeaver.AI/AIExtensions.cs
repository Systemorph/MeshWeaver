using MeshWeaver.AI.Persistence;
using MeshWeaver.AI.Plugins;
using MeshWeaver.AI.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// Extension methods for AI services
/// </summary>
public static class AIExtensions
{
    /// <summary>
    /// Adds the AI chat services including persistence and thread management.
    /// Uses in-memory thread manager by default.
    /// Call this after registering individual factory implementations (e.g., AddAzureOpenAI, AddAzureFoundryClaude).
    /// </summary>
    public static IServiceCollection AddAgentChatServices(this IServiceCollection services)
    {
        // Ensure ChatPersistenceService is registered
        services.AddMemoryChatPersistence();

        // Add thread manager (uses in-memory by default)
        services.AddInMemoryThreadManager();

        return services;
    }

    /// <summary>
    /// Adds the AI chat services with MeshDataSource persistence for threads.
    /// Stores chats in the parent object's namespace:
    /// - Chat metadata: {scope}/chats/{userId}/{threadId}
    /// - Messages: {scope}/chats/{userId}/{threadId}/messages/
    /// Requires IMeshStorage to be registered.
    /// </summary>
    public static IServiceCollection AddAgentChatServicesWithPersistence(this IServiceCollection services)
    {
        // Ensure ChatPersistenceService is registered
        services.AddMemoryChatPersistence();

        // Add MeshDataSource thread manager for persistent storage
        services.AddMeshDataSourceThreadManager();

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

    /// <summary>
    /// Registers the WebSearch plugin, making SearchWeb and FetchWebPage tools
    /// available to agents that declare "WebSearch" in their plugins frontmatter.
    /// </summary>
    public static IServiceCollection AddWebSearchPlugin(this IServiceCollection services, Action<WebSearchConfiguration>? configure = null)
    {
        if (configure != null)
            services.Configure(configure);
        else
            services.AddOptions<WebSearchConfiguration>();

        services.AddHttpClient<WebSearchPlugin>();
        services.AddSingleton<IAgentPlugin, WebSearchPlugin>();
        return services;
    }
}
