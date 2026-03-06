using MeshWeaver.AI.Persistence;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Threading;

/// <summary>
/// Extension methods for adding thread manager services.
/// </summary>
public static class ThreadManagerExtensions
{
    /// <summary>
    /// Adds the in-memory thread manager as a singleton service.
    /// </summary>
    public static IServiceCollection AddInMemoryThreadManager(this IServiceCollection services)
    {
        services.TryAddSingleton<IThreadManager, InMemoryThreadManager>();
        return services;
    }

    /// <summary>
    /// Adds the MeshDataSource thread manager that persists chats in partitions.
    /// Storage structure:
    /// - Chat metadata: {scope}/chats/{userId}/{threadId}
    /// - Messages: {scope}/chats/{userId}/{threadId}/messages/
    /// </summary>
    public static IServiceCollection AddMeshDataSourceThreadManager(this IServiceCollection services)
    {
        services.TryAddSingleton<IThreadManager>(sp =>
        {
            var accessService = sp.GetRequiredService<AccessService>();
            var hub = sp.GetRequiredService<IMessageHub>();
            var logger = sp.GetService<ILogger<MeshDataSourceThreadManager>>();
            return new MeshDataSourceThreadManager(accessService, hub, logger);
        });
        return services;
    }

    /// <summary>
    /// Adds a thread manager that adapts the existing IChatPersistenceService.
    /// Use this for backward compatibility with existing providers.
    /// </summary>
    public static IServiceCollection AddChatPersistenceThreadManager(this IServiceCollection services)
    {
        services.TryAddSingleton<IThreadManager>(sp =>
        {
            var persistenceService = sp.GetRequiredService<IChatPersistenceService>();
            return new ChatPersistenceThreadManagerAdapter(persistenceService);
        });
        return services;
    }

    /// <summary>
    /// Adds a custom thread manager implementation.
    /// </summary>
    public static IServiceCollection AddThreadManager<TImplementation>(this IServiceCollection services)
        where TImplementation : class, IThreadManager
    {
        services.TryAddSingleton<IThreadManager, TImplementation>();
        return services;
    }

    /// <summary>
    /// Adds a custom thread manager instance.
    /// </summary>
    public static IServiceCollection AddThreadManager(this IServiceCollection services, IThreadManager threadManager)
    {
        services.TryAddSingleton(threadManager);
        return services;
    }
}
