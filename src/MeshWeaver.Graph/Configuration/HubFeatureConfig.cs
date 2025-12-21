namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configuration for hub features.
/// Stored in _config/hubFeatures/{id}.json
/// </summary>
public record HubFeatureConfig
{
    /// <summary>
    /// Unique identifier for the hub feature configuration.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Enable mesh navigation (autocomplete + catalog view).
    /// </summary>
    public bool EnableMeshNavigation { get; init; } = true;

    /// <summary>
    /// Enable dynamic node type areas (auto-generated areas per node type).
    /// </summary>
    public bool EnableDynamicNodeTypeAreas { get; init; } = true;

    /// <summary>
    /// List of content collection IDs to add to this hub.
    /// References ContentCollectionConfig.Id values.
    /// </summary>
    public List<string>? ContentCollections { get; init; }
}
