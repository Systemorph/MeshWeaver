using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// Extension methods for adding chat persistence services
/// </summary>
public static class ChatPersistenceExtensions
{
    /// <summary>
    /// Adds in-memory chat persistence as a singleton service.
    /// Stores conversations and agent states per user in memory for the duration of the application.
    /// </summary>
    public static IServiceCollection AddMemoryChatPersistence(this IServiceCollection services)
    {
        return services.AddSingleton<IChatPersistenceService, InMemoryChatPersistenceService>();
    }
}
