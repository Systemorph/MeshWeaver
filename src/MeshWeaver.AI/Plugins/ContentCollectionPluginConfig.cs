using System.Collections.ObjectModel;
using MeshWeaver.ContentCollections;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Configuration for the SubmissionPlugin.
/// </summary>
public class ContentCollectionPluginConfig
{
    /// <summary>
    /// Collection of content collection configurations.
    /// </summary>
    public IReadOnlyCollection<ContentCollectionConfig> Collections { get; init; } = new ReadOnlyCollection<ContentCollectionConfig>(Array.Empty<ContentCollectionConfig>());

    /// <summary>
    /// Maps context identifiers to their corresponding collection configurations.
    /// </summary>
    public Func<AgentContext, ContentCollectionConfig>? ContextToConfigMap { get; init; }
}
