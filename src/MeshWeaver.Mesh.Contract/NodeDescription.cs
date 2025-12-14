using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>
/// Generic entity for nodes without a specific entity type.
/// Provides a markdown-enabled description field for rich node content.
/// This coexists with MeshNode.Description which serves as a summary.
/// </summary>
public record NodeDescription
{
    /// <summary>
    /// Unique identifier, typically matches the node's Prefix/Path.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Markdown-enabled description content.
    /// This is the rich content of the node, editable via Monaco editor.
    /// </summary>
    public string Description { get; init; } = string.Empty;
}
