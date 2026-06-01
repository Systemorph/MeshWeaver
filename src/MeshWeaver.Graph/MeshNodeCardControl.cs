using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Control for rendering a mesh node as a card.
/// Supports two modes:
/// 1. Default: renders a FluentCard with image/placeholder + title + description.
/// 2. ItemArea: delegates rendering to a LayoutAreaView for the specified area.
/// Navigation on click goes to /{NodePath}.
/// </summary>
public record MeshNodeCardControl(
    string NodePath,
    string? Title = null,
    string? Description = null,
    string? ImageUrl = null,
    string? ItemArea = null,
    object? DisableNavigation = null
) : UiControl<MeshNodeCardControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// Creates a card control from a MeshNode. If itemArea is set, the card
    /// delegates its content rendering to that layout area.
    /// </summary>
    public static MeshNodeCardControl FromNode(MeshNode? node, string fallbackPath, string? itemArea = null, bool disableNavigation = false)
    {
        var nodePath = node?.Path ?? fallbackPath;
        var title = node?.Name ?? fallbackPath;
        var imageUrl = MeshNodeThumbnailControl.GetImageUrlForNode(node);
        // Show a real one-line summary (the node's Description, or the markdown
        // Abstract when content is still typed). Falls back to the NodeType so
        // non-described nodes keep a meaningful label rather than a blank line.
        var description = node?.Description
            ?? (node?.Content as MarkdownContent)?.Abstract
            ?? node?.NodeType;

        return new MeshNodeCardControl(nodePath, title, description, imageUrl, itemArea, disableNavigation ? true : null);
    }
}
