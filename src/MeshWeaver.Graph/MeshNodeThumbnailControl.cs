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
    /// Gets the image URL for a node. Public so other builders can reuse.
    /// Priority: content.avatar > content.logo > content.icon > node.Icon > NodeType default.
    /// Handles both typed objects and JsonElement/Dictionary content.
    /// User-entered paths starting with <c>content:</c> or <c>content/</c> are resolved
    /// against the node's content collection; an inline <c>&lt;svg&gt;…&lt;/svg&gt;</c> value is
    /// returned verbatim (never treated as a path) so the client renders it inline.
    /// </summary>
    public static string? GetImageUrlForNode(MeshNode? node)
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
        var thumbnail = GetThumbnail(node.Content);
        if (!string.IsNullOrEmpty(thumbnail))
        {
            // Inline SVG thumbnail — return verbatim; it is markup, not a content-collection path.
            if (MeshNodeImageHelper.IsInlineSvg(thumbnail))
                return thumbnail;
            if (thumbnail.StartsWith("/") || thumbnail.StartsWith("http"))
                return thumbnail;
            var ns = node.Namespace;
            if (!string.IsNullOrEmpty(ns))
                return $"/static/storage/content/{ns}/{thumbnail}";
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
