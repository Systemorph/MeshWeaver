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

        return new MeshNodeThumbnailControl(nodePath, title, null, imageUrl, nodeType);
    }

    /// <summary>
    /// Gets the image URL for a node. Public so other builders can reuse.
    /// Priority: content.avatar > content.logo > node.Icon
    /// Handles both typed objects and JsonElement/Dictionary content.
    /// User-entered paths starting with <c>content:</c> or <c>content/</c> are resolved
    /// against the node's content collection.
    /// </summary>
    public static string? GetImageUrlForNode(MeshNode? node)
    {
        if (node == null)
            return null;

        // First check content properties (avatar, logo)
        if (node.Content != null)
        {
            // Try JsonElement first (common when deserializing from JSON)
            if (node.Content is System.Text.Json.JsonElement jsonElement)
            {
                var resolved = TryResolveJsonProperty(jsonElement, "avatar", node.Path)
                    ?? TryResolveJsonProperty(jsonElement, "Avatar", node.Path)
                    ?? TryResolveJsonProperty(jsonElement, "logo", node.Path)
                    ?? TryResolveJsonProperty(jsonElement, "Logo", node.Path);
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
            }
        }

        // Check MarkdownContent.Thumbnail — resolve relative path to absolute URL
        if (node.Content is MarkdownContent mc && !string.IsNullOrEmpty(mc.Thumbnail))
        {
            var thumbnail = mc.Thumbnail;
            if (thumbnail.StartsWith("/") || thumbnail.StartsWith("http"))
                return thumbnail;
            var ns = node.Namespace;
            if (!string.IsNullOrEmpty(ns))
                return $"/static/storage/content/{ns}/{thumbnail}";
        }

        // Fall back to node.Icon — resolves content: references, URLs, inline SVG, emojis
        return MeshNodeImageHelper.ResolveNodeIcon(node);
    }

    private static string? TryResolveJsonProperty(System.Text.Json.JsonElement element, string propertyName, string nodePath)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != System.Text.Json.JsonValueKind.String)
            return null;
        var value = prop.GetString();
        return string.IsNullOrEmpty(value) ? null : MeshNodeImageHelper.ResolveContentPath(value, nodePath);
    }
}
