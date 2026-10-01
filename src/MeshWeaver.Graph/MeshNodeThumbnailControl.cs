using MeshWeaver.ContentCollections;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Control for rendering a mesh node thumbnail card.
/// Used in grid layouts to display node information with image and click navigation.
/// </summary>
public record MeshNodeThumbnailControl(
    string NodePath,
    string Title,
    string? Description = null,
    string? ImageUrl = null,
    string? NodeType = null
) : UiControl<MeshNodeThumbnailControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
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
    public MeshNodeThumbnailControl BindTitle(object title) => this with { TitleBinding = title };

    /// <summary>Returns a copy whose description is <paramref name="description"/> — a literal or a
    /// pointer the renderer follows. See <see cref="DescriptionBinding"/>.</summary>
    /// <param name="description">The description, or a pointer to it.</param>
    /// <returns>A new instance with the updated <see cref="DescriptionBinding"/>.</returns>
    public MeshNodeThumbnailControl BindDescription(object description) => this with { DescriptionBinding = description };

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
    public MeshNodeThumbnailControl BindToNode(string dataNodePath, string? titleField, string? descriptionField = null, bool bindContent = true) =>
        this with
        {
            TitleBinding = titleField is null ? TitleBinding : new MeshWeaver.Data.JsonPointerReference(titleField),
            DescriptionBinding = descriptionField is null ? DescriptionBinding : new MeshWeaver.Data.JsonPointerReference(descriptionField),
            DataContext = MeshWeaver.Data.LayoutAreaReference.GetMeshNodeDataContext(dataNodePath, bindContent),
        };

    /// <summary>
    /// Creates a thumbnail control from a MeshNode.
    /// </summary>
    public static MeshNodeThumbnailControl FromNode(MeshNode? node, string fallbackPath)
    {
        var nodePath = node?.Path ?? fallbackPath;
        var title = node?.Name ?? fallbackPath;
        var imageUrl = GetImageUrlForNode(node);
        var nodeType = node?.NodeType;
        // GetAbstract handles BOTH typed MarkdownContent AND the degraded JsonElement frame —
        // `as MarkdownContent` → null on JsonElement frames, so the description (and thus the whole
        // control record) would alternate between Abstract and node.Description across frames →
        // dedup never fires → thumbnail render storm.
        var description = node?.Description ?? GetAbstract(node?.Content);

        return new MeshNodeThumbnailControl(nodePath, title, description, imageUrl, nodeType);
    }

    /// <summary>
    /// Whether a fault raised while streaming this thumbnail's SUBJECT node should be SURFACED as an
    /// error (a genuine infrastructure fault → a single toast + log) rather than treated as a benign,
    /// expected fallback.
    ///
    /// <para>An access-denied read is DENIED BY DESIGN here (issue #434): an <c>AccessAssignment</c> row's
    /// subject frequently lives in a partition the viewer is not a member of — two users can share access
    /// to a node without being able to read each other's user partitions. The row already carries the
    /// subject id + roles, so the card renders its seeded initials-avatar fallback; the denial is a normal
    /// state, NOT "Something went wrong". Delegates to <see cref="AreaErrorClassifier.IsExpectedUserActionFailure"/>
    /// (true for <see cref="UnauthorizedAccessException"/> / access-denied delivery failures), so only a
    /// genuine infra fault surfaces. Pure — unit-tested without a renderer or a circuit.</para>
    /// </summary>
    public static bool ShouldSurfaceStreamError(Exception? ex)
        => !AreaErrorClassifier.IsExpectedUserActionFailure(ex);

    /// <summary>
    /// Gets the image URL for a node. Public so other builders can reuse.
    /// Priority: content.avatar > content.logo > content.icon > MarkdownContent.Thumbnail > node.Icon > NodeType default.
    /// Handles both typed objects and JsonElement/Dictionary content.
    /// User-entered paths starting with <c>content:</c> or <c>content/</c> are resolved
    /// against the node's content collection; an inline <c>&lt;svg&gt;…&lt;/svg&gt;</c> value is
    /// returned verbatim (never treated as a path) so the client renders it inline.
    ///
    /// <para><paramref name="includeThumbnail"/> selects between the two SHAPES this resolution
    /// serves. A thumbnail tile is poster-shaped, so it keeps
    /// <see cref="MarkdownContent.Thumbnail"/> in the chain (the default). A CARD is not: its
    /// image box is a fixed 48 px square with <c>object-fit: cover</c>, and a wide banner cropped
    /// into that yields a meaningless sliver — so <see cref="MeshNodeCardControl"/> passes
    /// <c>false</c> and falls straight through to the node's own icon, which is drawn for exactly
    /// that size. An explicitly authored <c>avatar</c>/<c>logo</c>/<c>icon</c> on the content still
    /// wins in both shapes.</para>
    /// </summary>
    /// <param name="node">The node to resolve an image for.</param>
    /// <param name="includeThumbnail">Whether <see cref="MarkdownContent.Thumbnail"/> (the wide
    /// poster) participates. <c>true</c> for poster-shaped tiles, <c>false</c> for icon-shaped
    /// cards.</param>
    public static string? GetImageUrlForNode(MeshNode? node, bool includeThumbnail = true)
    {
        if (node == null)
            return null;

        // First check content properties (avatar, logo, icon). `icon`/`Icon` is included so a
        // content-carried icon — commonly an inline <svg> — surfaces on the card instead of being
        // dropped in favour of the generic NodeType default. ResolveContentPath returns inline SVG
        // and URLs verbatim, resolves content:/content/ references, and returns null for legacy
        // Fluent icon names (so those correctly fall through to node.Icon / the NodeType default).
        if (node.Content != null)
        {
            // Try JsonElement first (common when deserializing from JSON)
            if (node.Content is System.Text.Json.JsonElement jsonElement)
            {
                var resolved = TryResolveJsonProperty(jsonElement, "avatar", node.Path)
                    ?? TryResolveJsonProperty(jsonElement, "Avatar", node.Path)
                    ?? TryResolveJsonProperty(jsonElement, "logo", node.Path)
                    ?? TryResolveJsonProperty(jsonElement, "Logo", node.Path)
                    ?? TryResolveJsonProperty(jsonElement, "icon", node.Path)
                    ?? TryResolveJsonProperty(jsonElement, "Icon", node.Path);
                if (resolved != null)
                    return resolved;
            }
            // Try Dictionary<string, object>
            else if (node.Content is IDictionary<string, object> dict)
            {
                if (dict.TryGetValue("avatar", out var avatar) || dict.TryGetValue("Avatar", out avatar))
                {
                    var resolved = MeshNodeImageHelper.ResolveContentPath(avatar?.ToString(), node.Path);
                    if (!string.IsNullOrEmpty(resolved))
                        return resolved;
                }
                if (dict.TryGetValue("logo", out var logo) || dict.TryGetValue("Logo", out logo))
                {
                    var resolved = MeshNodeImageHelper.ResolveContentPath(logo?.ToString(), node.Path);
                    if (!string.IsNullOrEmpty(resolved))
                        return resolved;
                }
                if (dict.TryGetValue("icon", out var contentIcon) || dict.TryGetValue("Icon", out contentIcon))
                {
                    var resolved = MeshNodeImageHelper.ResolveContentPath(contentIcon?.ToString(), node.Path);
                    if (!string.IsNullOrEmpty(resolved))
                        return resolved;
                }
            }
            else
            {
                // Fall back to reflection for typed objects
                var avatarProperty = node.Content.GetType().GetProperty("Avatar");
                if (avatarProperty != null)
                {
                    var resolved = MeshNodeImageHelper.ResolveContentPath(
                        avatarProperty.GetValue(node.Content) as string, node.Path);
                    if (!string.IsNullOrEmpty(resolved))
                        return resolved;
                }

                var logoProperty = node.Content.GetType().GetProperty("Logo");
                if (logoProperty != null)
                {
                    var resolved = MeshNodeImageHelper.ResolveContentPath(
                        logoProperty.GetValue(node.Content) as string, node.Path);
                    if (!string.IsNullOrEmpty(resolved))
                        return resolved;
                }

                var iconProperty = node.Content.GetType().GetProperty("Icon");
                if (iconProperty != null)
                {
                    var resolved = MeshNodeImageHelper.ResolveContentPath(
                        iconProperty.GetValue(node.Content) as string, node.Path);
                    if (!string.IsNullOrEmpty(resolved))
                        return resolved;
                }
            }
        }

        // Check MarkdownContent.Thumbnail — resolve relative path to absolute URL. GetThumbnail
        // reads the value from BOTH the typed MarkdownContent AND the degraded JsonElement frame,
        // so the resolved image URL doesn't alternate (typed → thumbnail vs JsonElement → node.Icon)
        // across frames and storm the card.
        var thumbnail = includeThumbnail ? GetThumbnail(node.Content) : null;
        if (!string.IsNullOrEmpty(thumbnail))
        {
            // Inline SVG thumbnail — return verbatim; it is markup, not a content-collection path.
            if (MeshNodeImageHelper.IsInlineSvg(thumbnail))
                return thumbnail;
            if (thumbnail.StartsWith("/") || thumbnail.StartsWith("http"))
                return thumbnail;
            var ns = node.Namespace;
            if (!string.IsNullOrEmpty(ns))
                // Access-controlled content route — NEVER /static (issue #587): a private
                // Space's thumbnail must not be world-readable by URL.
                return ContentCollectionsExtensions.GetNodeContentFileUrl(ns, thumbnail);
        }

        // Fall back to node.Icon — resolves content: references, URLs, inline SVG, emojis
        return MeshNodeImageHelper.ResolveNodeIcon(node);
    }

    /// <summary>
    /// Reads <see cref="MarkdownContent.Abstract"/> from either a typed instance or a degraded
    /// <see cref="System.Text.Json.JsonElement"/> frame (cache / cross-hub / change-feed reads). The
    /// JsonElement branch mirrors the avatar/logo handling above so the projection is frame-stable.
    /// </summary>
    private static string? GetAbstract(object? content) => content switch
    {
        MarkdownContent mc => mc.Abstract,
        System.Text.Json.JsonElement je => TryGetJsonString(je, "abstract") ?? TryGetJsonString(je, "Abstract"),
        _ => null
    };

    /// <summary>
    /// Reads <see cref="MarkdownContent.Thumbnail"/> from either a typed instance or a degraded
    /// <see cref="System.Text.Json.JsonElement"/> frame, so the resolved image URL is frame-stable.
    /// </summary>
    private static string? GetThumbnail(object? content) => content switch
    {
        MarkdownContent mc => mc.Thumbnail,
        System.Text.Json.JsonElement je => TryGetJsonString(je, "thumbnail") ?? TryGetJsonString(je, "Thumbnail"),
        _ => null
    };

    private static string? TryGetJsonString(System.Text.Json.JsonElement element, string propertyName)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var prop)
            || prop.ValueKind != System.Text.Json.JsonValueKind.String)
            return null;
        var value = prop.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? TryResolveJsonProperty(System.Text.Json.JsonElement element, string propertyName, string nodePath)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != System.Text.Json.JsonValueKind.String)
            return null;
        var value = prop.GetString();
        return string.IsNullOrEmpty(value) ? null : MeshNodeImageHelper.ResolveContentPath(value, nodePath);
    }
}
