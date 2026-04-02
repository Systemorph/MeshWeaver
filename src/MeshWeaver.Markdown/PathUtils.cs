namespace MeshWeaver.Markdown;

/// <summary>
/// Utility methods for resolving relative paths against a current node path.
/// Relative paths resolve from the current node (treating it as a container).
/// Absolute paths start with '/'.
/// </summary>
public static class PathUtils
{
    /// <summary>
    /// Resolves a relative path against the current node path.
    /// Returns the path unchanged if it's already absolute, external, or an anchor.
    /// </summary>
    /// <param name="path">The path to resolve (may be relative or absolute)</param>
    /// <param name="currentNodePath">The full path of the current node (e.g., "Doc/Architecture")</param>
    /// <returns>The resolved absolute path (without leading '/')</returns>
    public static string ResolveRelativePath(string path, string? currentNodePath)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.StartsWith("http") || path.StartsWith('#') || path.StartsWith("mailto:"))
            return path;
        if (string.IsNullOrEmpty(currentNodePath))
            return path;

        // Handle ../ segments (go up to parent)
        var basePath = currentNodePath;
        while (path.StartsWith("../"))
        {
            var lastSlash = basePath.LastIndexOf('/');
            basePath = lastSlash > 0 ? basePath[..lastSlash] : "";
            path = path[3..];
        }

        // Handle ./ (current directory, just strip it)
        if (path.StartsWith("./"))
            path = path[2..];

        if (string.IsNullOrEmpty(path))
            return basePath;

        return string.IsNullOrEmpty(basePath) ? path : $"{basePath}/{path}";
    }
}
