using MeshWeaver.Layout;
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

        return new MeshNodeCardControl(nodePath, title, node?.NodeType, imageUrl, itemArea, disableNavigation ? true : null);
    }
}
