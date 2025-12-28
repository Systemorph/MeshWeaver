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
    /// The namespace in which the type is defined (is also partition)
    /// </summary>
    public required string Namespace { get; init; }

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
    /// Default values for initializing new instances of this type.
    /// Keys are property names, values are default values.
    /// </summary>
    public Dictionary<string, object>? DefaultValues { get; init; }

    /// <summary>
    /// RSQL query for getting "children" to display in the Details view.
    /// When set, uses QueryAsync instead of GetChildrenAsync.
    /// Example: "nodeType==Type/Organization;$scope=descendants" finds all nodes
    /// of type "Type/Organization" anywhere in the hierarchy.
    /// If null, defaults to "$scope=children" (direct children only).
    /// </summary>
    public string? ChildrenQuery { get; init; }

    /// <summary>
    /// Lambda expression for configuring the message hub.
    /// Signature: Func&lt;MessageHubConfiguration, MessageHubConfiguration&gt;
    /// Example: "config => config.AddData(d => d.AddSource(s => s.WithType&lt;Person&gt;()))"
    /// Should call WithDefaultViews() to add standard views (Details, Edit, Thumbnail, etc).
    /// </summary>
    public string? HubConfiguration { get; init; }

    /// <summary>
    /// Lambda expression source code for hub configuration.
    /// Signature: Func&lt;MessageHubConfiguration, MessageHubConfiguration&gt;
    /// Example: "config => config.AddData(d => d.AddSource(...))"
    /// This is compiled at runtime and assigned to HubConfiguration.
    /// </summary>
    public string? Configuration { get; init; }

    /// <summary>
    /// List of NodeType paths this type depends on.
    /// Used for Monaco autocomplete to include types from dependencies.
    /// Example: ["type/Person", "type/Organization"]
    /// </summary>
    public List<string>? Dependencies { get; init; }
}
