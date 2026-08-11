using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Control for rendering a mesh node as a card.
/// Supports two modes:
/// 1. Default: renders a FluentCard with image/placeholder + title + description.
/// 2. ItemArea: delegates rendering to a LayoutAreaView for the specified area.
/// Navigation on click goes to /{NodePath} — unless <paramref name="Href"/> carries an absolute
/// EXTERNAL URL, which then wins and opens in a new tab (the <c>OgCard</c> link-preview cards).
/// </summary>
public record MeshNodeCardControl(
    string NodePath,
    string? Title = null,
    string? Description = null,
    string? ImageUrl = null,
    string? ItemArea = null,
    object? DisableNavigation = null,
    string? Href = null
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
        // The ICON, never the poster: the card's image box is a fixed 48 px square with
        // object-fit: cover, so a wide MarkdownContent.Thumbnail banner cropped into it is a
        // meaningless sliver. Skipping it falls through to the node's own icon — an inline SVG
        // drawn in currentColor, sized for exactly this box. MeshNodeThumbnailControl is the
        // poster-shaped control and keeps the thumbnail.
        var imageUrl = MeshNodeThumbnailControl.GetImageUrlForNode(node, includeThumbnail: false);
        // Databind the card subtitle to the node's Description (blank when unset) —
        // no NodeType fallback, so the line reflects the real description, nothing else.
        var description = node?.Description;

        return new MeshNodeCardControl(nodePath, title, description, imageUrl, itemArea, disableNavigation ? true : null);
    }
}
