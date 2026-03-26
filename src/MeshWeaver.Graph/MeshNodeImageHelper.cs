namespace MeshWeaver.Graph;

public static class MeshNodeImageHelper
{
    /// <summary>
    /// Returns the icon value for rendering. Accepts URLs, data URIs, inline SVG, and emojis.
    /// Returns null only for legacy Fluent icon names (PascalCase ASCII, e.g. "Document").
    /// Callers are responsible for detecting the type (SVG, URL, emoji) and rendering appropriately.
    /// </summary>
    public static string? GetIconForRendering(string? icon)
    {
        if (string.IsNullOrEmpty(icon))
            return null;
        // Filter out legacy Fluent icon names (PascalCase ASCII words like "Document", "People")
        if (IsFluentIconName(icon))
            return null;
        return icon;
    }

    /// <summary>
    /// A Fluent icon name is purely ASCII letters starting with uppercase (e.g., "Chat", "ArrowLeft").
    /// </summary>
    public static bool IsFluentIconName(string value)
        => !string.IsNullOrEmpty(value)
           && char.IsAsciiLetterUpper(value[0])
           && value.All(char.IsAsciiLetter);

    /// <summary>
    /// Returns true if the icon value is an inline SVG string.
    /// </summary>
    public static bool IsInlineSvg(string? icon)
        => !string.IsNullOrEmpty(icon) && icon.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if the icon value is a URL or data URI (renderable as img src).
    /// </summary>
    public static bool IsImageUrl(string? icon)
        => !string.IsNullOrEmpty(icon) && (icon.Contains('/') || icon.StartsWith("data:"));

    /// <summary>
    /// Returns true if the icon value is an emoji or other non-URL, non-SVG, non-Fluent text.
    /// </summary>
    public static bool IsEmoji(string? icon)
        => !string.IsNullOrEmpty(icon) && !IsImageUrl(icon) && !IsInlineSvg(icon) && !IsFluentIconName(icon);

    /// <summary>
    /// Legacy method — returns the icon only if it's an image URL.
    /// Prefer <see cref="GetIconForRendering"/> which also returns SVG and emoji icons.
    /// </summary>
    [Obsolete("Use GetIconForRendering instead")]
    public static string? GetIconAsImageUrl(string? icon)
    {
        if (string.IsNullOrEmpty(icon))
            return null;
        return icon.Contains('/') || icon.StartsWith("data:") ? icon : null;
    }
}
