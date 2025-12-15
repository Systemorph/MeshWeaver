using MeshWeaver.Layout;

namespace MeshWeaver.Graph;

/// <summary>
/// Control for editing mesh node metadata and content.
/// Supports editing the last path segment (with move operation) and
/// node content (Story.Text or Article content with Monaco editor).
/// </summary>
public record MeshNodeEditorControl() : UiControl<MeshNodeEditorControl>("MeshWeaver.Graph", "1.0.0")
{
    /// <summary>
    /// The path of the node being edited.
    /// </summary>
    public string NodePath { get; init; } = string.Empty;

    /// <summary>
    /// The node type (e.g., "story", "article") - determines editor mode.
    /// </summary>
    public string? NodeType { get; init; }
}
