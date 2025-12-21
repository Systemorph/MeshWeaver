namespace MeshWeaver.Graph.Configuration;

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
}
