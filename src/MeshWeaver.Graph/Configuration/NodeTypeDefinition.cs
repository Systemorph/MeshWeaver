using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Content type for NodeType MeshNodes.
/// Contains minimal metadata only - detailed definitions (DataModel, LayoutAreas)
/// are stored as separate files in the node's partition folder.
/// </summary>
public record NodeTypeDefinition
{
    /// <summary>
    /// The node type identifier (e.g., "story", "project").
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Display name for the type in UI.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Icon name for UI (e.g., "Document", "Folder").
    /// </summary>
    public string? IconName { get; init; }

    /// <summary>
    /// Description of this node type.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Display order for sorting in UI lists.
    /// </summary>
    public int DisplayOrder { get; init; }

    /// <summary>
    /// Content collection mappings for this node type.
    /// Each mapping configures a content collection that will be available in the hub.
    /// </summary>
    public List<ContentCollectionMapping>? ContentCollections { get; init; }

    /// <summary>
    /// Default values for initializing new instances of this type.
    /// Keys are property names, values are default values.
    /// </summary>
    public Dictionary<string, object>? DefaultValues { get; init; }
}
