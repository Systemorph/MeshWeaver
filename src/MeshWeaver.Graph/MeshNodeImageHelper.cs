namespace MeshWeaver.Graph;

public static class MeshNodeImageHelper
{
    /// <summary>
    /// Returns the icon string as an image URL only if it looks like an actual URL or data URI.
    /// Returns null for Fluent icon names (e.g. "Document", "People") that should not be used as img src.
    /// </summary>
    public static string? GetIconAsImageUrl(string? icon)
    {
        if (string.IsNullOrEmpty(icon))
            return null;
        return icon.Contains('/') || icon.StartsWith("data:") ? icon : null;
    }
}
