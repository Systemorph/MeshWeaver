namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configures a content collection mapping for a node type.
/// The collection will be configured in the hub when nodes of this type are created.
/// </summary>
public record ContentCollectionMapping
{
    /// <summary>
    /// The name of the content collection to register in the hub.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The base collection name (e.g., "graph").
    /// This corresponds to the root of the data directory or Azure container.
    /// </summary>
    public required string BaseCollection { get; init; }

    /// <summary>
    /// Sub-path under the base collection.
    /// Can include {id} placeholder which will be replaced with the node's address id.
    /// Examples: "logos", "persons/{id}", "submission/{id}"
    /// </summary>
    public required string SubPath { get; init; }
}

/// <summary>
/// Maps a node type to its data model and hub features.
/// Stored in _config/nodeTypes/{nodeType}.json
/// </summary>
public record NodeTypeConfig
{
    /// <summary>
    /// The node type identifier (matches MeshNode.NodeType).
    /// </summary>
    public required string NodeType { get; init; }

    /// <summary>
    /// Reference to the DataModel.Id that defines the content type.
    /// </summary>
    public required string DataModelId { get; init; }

    /// <summary>
    /// Reference to the HubFeatureConfig.Id (optional).
    /// If not specified, uses default hub features.
    /// </summary>
    public string? HubFeatureId { get; init; }

    /// <summary>
    /// Display name (overrides DataModel.DisplayName if set).
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Icon name (overrides DataModel.IconName if set).
    /// </summary>
    public string? IconName { get; init; }

    /// <summary>
    /// Description (overrides DataModel.Description if set).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Display order (overrides DataModel.DisplayOrder if set).
    /// </summary>
    public int? DisplayOrder { get; init; }

    /// <summary>
    /// Content collection mappings for this node type.
    /// Each mapping configures a content collection that will be available in the hub.
    /// The SubPath can include {id} placeholder for dynamic paths based on node address.
    /// </summary>
    public List<ContentCollectionMapping>? ContentCollections { get; init; }
}
