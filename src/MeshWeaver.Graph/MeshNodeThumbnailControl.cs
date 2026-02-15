using MeshWeaver.Layout;
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
        var description = node?.Description;
        var imageUrl = GetImageUrl(node);
        var nodeType = node?.NodeType;

        return new MeshNodeThumbnailControl(nodePath, title, description, imageUrl, nodeType);
    }

    /// <summary>
    /// Gets the image URL for a node.
    /// Priority: content.avatar > content.logo > node.Icon
    /// Handles both typed objects and JsonElement/Dictionary content.
    /// </summary>
    private static string? GetImageUrl(MeshNode? node)
    {
        if (node == null)
            return null;

        // First check content properties (avatar, logo)
        if (node.Content != null)
        {
            // Try JsonElement first (common when deserializing from JSON)
            if (node.Content is System.Text.Json.JsonElement jsonElement)
            {
                // Try avatar
                if (jsonElement.TryGetProperty("avatar", out var avatarProp) && avatarProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var avatar = avatarProp.GetString();
                    if (!string.IsNullOrEmpty(avatar))
                        return avatar;
                }
                // Try Avatar (PascalCase)
                if (jsonElement.TryGetProperty("Avatar", out var avatarPascalProp) && avatarPascalProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var avatar = avatarPascalProp.GetString();
                    if (!string.IsNullOrEmpty(avatar))
                        return avatar;
                }
                // Try logo
                if (jsonElement.TryGetProperty("logo", out var logoProp) && logoProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var logo = logoProp.GetString();
                    if (!string.IsNullOrEmpty(logo))
                        return logo;
                }
                // Try Logo (PascalCase)
                if (jsonElement.TryGetProperty("Logo", out var logoPascalProp) && logoPascalProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var logo = logoPascalProp.GetString();
                    if (!string.IsNullOrEmpty(logo))
                        return logo;
                }
            }
            // Try Dictionary<string, object>
            else if (node.Content is IDictionary<string, object> dict)
            {
                if (dict.TryGetValue("avatar", out var avatar) || dict.TryGetValue("Avatar", out avatar))
                {
                    var avatarStr = avatar?.ToString();
                    if (!string.IsNullOrEmpty(avatarStr))
                        return avatarStr;
                }
                if (dict.TryGetValue("logo", out var logo) || dict.TryGetValue("Logo", out logo))
                {
                    var logoStr = logo?.ToString();
                    if (!string.IsNullOrEmpty(logoStr))
                        return logoStr;
                }
            }
            else
            {
                // Fall back to reflection for typed objects
                // Try to get Avatar property (for Person)
                var avatarProperty = node.Content.GetType().GetProperty("Avatar");
                if (avatarProperty != null)
                {
                    var avatarValue = avatarProperty.GetValue(node.Content) as string;
                    if (!string.IsNullOrEmpty(avatarValue))
                        return avatarValue;
                }

                // Try to get Logo property (for Organization)
                var logoProperty = node.Content.GetType().GetProperty("Logo");
                if (logoProperty != null)
                {
                    var logoValue = logoProperty.GetValue(node.Content) as string;
                    if (!string.IsNullOrEmpty(logoValue))
                        return logoValue;
                }
            }
        }

        // Fall back to node.Icon only if it looks like an image URL (not a Fluent icon name)
        return MeshNodeImageHelper.GetIconAsImageUrl(node.Icon);
    }
}
