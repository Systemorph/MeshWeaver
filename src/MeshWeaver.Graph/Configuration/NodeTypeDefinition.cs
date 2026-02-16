using System.ComponentModel.DataAnnotations;
using MeshWeaver.ContentCollections;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Content type for NodeType MeshNodes.
/// Contains minimal metadata only - detailed definitions (DataModel, LayoutAreas)
/// are stored as separate files in the node's partition folder.
/// </summary>
public record NodeTypeDefinition
{
    /// <summary>
    /// Unique identifier for this node type definition.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// The namespace in which the type is defined (is also partition)
    /// </summary>
    public required string Namespace { get; init; }

    /// <summary>
    /// Display name for the type in UI.
    /// </summary>
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string? DisplayName { get; init; }

    /// <summary>
    /// Icon URL or identifier for UI. Can be a path to an SVG file.
    /// </summary>
    [MeshNodeProperty(nameof(MeshNode.Icon))]
    public string? Icon { get; init; }

    /// <summary>
    /// Emoji character to use as icon. Takes precedence over Icon if both are set.
    /// Example: "📝", "📁", "🎯"
    /// </summary>
    public string? Emoji { get; init; }

    /// <summary>
    /// Description of this node type.
    /// </summary>
    [MeshNodeProperty(nameof(MeshNode.Description))]
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
    /// Query string for getting "children" to display in the Details view.
    /// Uses IMeshQuery with the specified query pattern.
    /// Example: "nodeType:Type/Organization scope:descendants" finds all nodes
    /// of type "Type/Organization" anywhere in the hierarchy.
    /// If null, defaults to "scope:children" (direct children only).
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

    /// <summary>
    /// Content collections to register for this node type.
    /// Each collection can be FileSystem, EmbeddedResource, or Hub-based.
    /// The collections are registered via extension methods in the generated hub configuration.
    /// </summary>
    public List<ContentCollectionConfig>? ContentCollections { get; init; }

    /// <summary>
    /// Whether to show children section in the Details view.
    /// Default: true (show children if ChildrenQuery is set or has children).
    /// </summary>
    public bool ShowChildrenInDetails { get; init; } = true;

    /// <summary>
    /// Maximum number of children to show in the Details view before "Show more" link.
    /// Default: 10.
    /// </summary>
    public int DetailsChildrenLimit { get; init; } = 10;

    /// <summary>
    /// Explicit list of NodeType paths that can be created from instances of this type.
    /// If null, computed automatically from hierarchy (child NodeTypes).
    /// Example: ["ACME/Project/Todo", "ACME/Project/Story"]
    /// </summary>
    public List<string>? CreatableTypes { get; init; }

    /// <summary>
    /// If true, includes global types (Markdown, NodeType) in creatable list.
    /// Default: true.
    /// </summary>
    public bool IncludeGlobalTypes { get; init; } = true;

    /// <summary>
    /// Maximum width for the page content area (e.g., "960px", "1200px", "100%").
    /// Applied as CSS max-width on the outer container.
    /// If null, defaults to "100%" (no constraint).
    /// </summary>
    public string? PageMaxWidth { get; init; }
}
