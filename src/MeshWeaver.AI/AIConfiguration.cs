using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// Configuration class for AI services
/// </summary>
public class AIConfiguration
{
    private readonly IServiceCollection _services;
    private readonly List<Action<ChatOptions, IServiceProvider>> _chatOptionEnrichments = new();

    public AIConfiguration(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Adds a chat option enrichment function
    /// </summary>
    public AIConfiguration WithChatOptionEnrichment(Action<ChatOptions, IServiceProvider> enrichment)
    {
        _chatOptionEnrichments.Add(enrichment);
        return this;
    }

    /// <summary>
    /// Gets all registered chat option enrichments
    /// </summary>
    internal IReadOnlyList<Action<ChatOptions, IServiceProvider>> GetChatOptionEnrichments() => _chatOptionEnrichments;

    /// <summary>
    /// Gets the service collection
    /// </summary>
    internal IServiceCollection Services => _services;
}

/// <summary>
/// Extension methods for AI configuration
/// </summary>
public static class AIConfigurationExtensions
{    /// <summary>
     /// Adds AI services to the service collection
     /// </summary>
    public static IServiceCollection AddAI(this IServiceCollection services, Func<AIConfiguration, AIConfiguration> configure)
    {
        var config = new AIConfiguration(services);
        configure(config);

        // Store the configuration for later use
        services.AddSingleton(config);

        return services;
    }
}
