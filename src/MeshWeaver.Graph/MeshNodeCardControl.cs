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
    /// BINDABLE title: a literal or a <see cref="MeshWeaver.Data.JsonPointerReference"/> the renderer resolves
    /// against the control's DataContext (node-bound via
    /// <see cref="MeshWeaver.Data.LayoutAreaReference.GetMeshNodeDataContext"/>, or an ordinary <c>/data</c>
    /// context) and FOLLOWS. When it resolves to a non-empty value it wins over the literal title and
    /// over the <see cref="NodePath"/> node's name; while it has no value the card falls back to them.
    /// See Doc/GUI/DataBinding → "Binding a rich control to a node field".
    /// </summary>
    public object? TitleBinding { get; init; }

    /// <summary>
    /// BINDABLE description — the twin of <see cref="TitleBinding"/>: a non-empty resolved value wins
    /// over the literal description; while it has no value the card falls back to it.
    /// </summary>
    public object? DescriptionBinding { get; init; }

    /// <summary>Returns a copy whose title is <paramref name="title"/> — a literal or a pointer the
    /// renderer follows. See <see cref="TitleBinding"/>.</summary>
    /// <param name="title">The title, or a pointer to it.</param>
    /// <returns>A new instance with the updated <see cref="TitleBinding"/>.</returns>
    public MeshNodeCardControl BindTitle(object title) => this with { TitleBinding = title };

    /// <summary>Returns a copy whose description is <paramref name="description"/> — a literal or a
    /// pointer the renderer follows. See <see cref="DescriptionBinding"/>.</summary>
    /// <param name="description">The description, or a pointer to it.</param>
    /// <returns>A new instance with the updated <see cref="DescriptionBinding"/>.</returns>
    public MeshNodeCardControl BindDescription(object description) => this with { DescriptionBinding = description };

    /// <summary>
    /// Returns a copy whose title and/or description are read live from fields of the node at
    /// <paramref name="dataNodePath"/> — which need not be the card's own <see cref="NodePath"/>
    /// (an access-assignment row shows the SUBJECT's card, captioned from the assignment). The
    /// producing area renders the card at once and never loads either node.
    /// </summary>
    /// <param name="dataNodePath">The node whose fields caption the card.</param>
    /// <param name="titleField">The field bound to the title, or <c>null</c> to leave the title as is.</param>
    /// <param name="descriptionField">The field bound to the description, or <c>null</c> to leave it as is.</param>
    /// <param name="bindContent"><c>true</c> (default) resolves the fields against the node's
    /// <c>Content</c>; <c>false</c> against its top-level fields (<c>Name</c>, <c>Description</c>).</param>
    /// <returns>A new instance bound to the node fields.</returns>
    public MeshNodeCardControl BindToNode(string dataNodePath, string? titleField, string? descriptionField = null, bool bindContent = true) =>
        this with
        {
            TitleBinding = titleField is null ? TitleBinding : new MeshWeaver.Data.JsonPointerReference(titleField),
            DescriptionBinding = descriptionField is null ? DescriptionBinding : new MeshWeaver.Data.JsonPointerReference(descriptionField),
            DataContext = MeshWeaver.Data.LayoutAreaReference.GetMeshNodeDataContext(dataNodePath, bindContent),
        };

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
