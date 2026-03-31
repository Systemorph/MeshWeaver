using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Utility for determining if a changed path should trigger notifications for a query
/// based on the query's scope.
/// </summary>
public static class PathMatcher
{
    /// <summary>
    /// Determines if a notification should be sent for a change at the given path,
    /// based on the query's base path and scope.
    /// </summary>
    /// <param name="changedPath">The normalized path where the change occurred.</param>
    /// <param name="queryBasePath">The normalized base path from the query.</param>
    /// <param name="scope">The query scope determining which paths are monitored.</param>
    /// <returns>True if the change should trigger a notification for this query.</returns>
    public static bool ShouldNotify(string changedPath, string queryBasePath, QueryScope scope)
    {
        var normalizedChanged = NormalizePath(changedPath);
        var normalizedBase = NormalizePath(queryBasePath);

        return scope switch
        {
            // Only exact path matches
            QueryScope.Exact => PathEquals(normalizedChanged, normalizedBase),

            // Direct children only (one level deep)
            QueryScope.Children => IsDirectChild(normalizedChanged, normalizedBase),

            // All descendants recursively (excludes self)
            QueryScope.Descendants => IsDescendant(normalizedChanged, normalizedBase),

            // All ancestors (excludes self)
            QueryScope.Ancestors => IsAncestor(normalizedChanged, normalizedBase),

            // Self + all descendants
            QueryScope.Subtree => PathEquals(normalizedChanged, normalizedBase) ||
                                  IsDescendant(normalizedChanged, normalizedBase),

            // Self + all ancestors
            QueryScope.AncestorsAndSelf => PathEquals(normalizedChanged, normalizedBase) ||
                                           IsAncestor(normalizedChanged, normalizedBase),

            // Ancestors + self + descendants
            QueryScope.Hierarchy => PathEquals(normalizedChanged, normalizedBase) ||
                                    IsAncestor(normalizedChanged, normalizedBase) ||
                                    IsDescendant(normalizedChanged, normalizedBase),

            _ => false
        };
    }

    /// <summary>
    /// Checks if two paths are equal (case-insensitive).
    /// </summary>
    private static bool PathEquals(string path1, string path2)
    {
        return string.Equals(path1, path2, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if changedPath is a direct child of basePath (exactly one level deeper).
    /// </summary>
    private static bool IsDirectChild(string changedPath, string basePath)
    {
        if (string.IsNullOrEmpty(changedPath))
            return false;

        var changedSegments = GetSegments(changedPath);
        var baseSegments = GetSegments(basePath);

        // Child must be exactly one level deeper
        if (changedSegments.Length != baseSegments.Length + 1)
            return false;

        // All base segments must match
        for (int i = 0; i < baseSegments.Length; i++)
        {
            if (!string.Equals(changedSegments[i], baseSegments[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if changedPath is a descendant of basePath (any level deeper).
    /// </summary>
    private static bool IsDescendant(string changedPath, string basePath)
    {
        if (string.IsNullOrEmpty(changedPath))
            return false;

        // Empty base means root - everything is a descendant
        if (string.IsNullOrEmpty(basePath))
            return true;

        // Must start with basePath followed by a separator
        return changedPath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if changedPath is an ancestor of basePath.
    /// </summary>
    private static bool IsAncestor(string changedPath, string basePath)
    {
        if (string.IsNullOrEmpty(basePath))
            return false;

        // Empty changed path is the root - ancestor of everything
        if (string.IsNullOrEmpty(changedPath))
            return true;

        // basePath must start with changedPath followed by a separator
        return basePath.StartsWith(changedPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes a path by trimming slashes.
    /// </summary>
    private static string NormalizePath(string? path) =>
        path?.Trim('/') ?? "";

    /// <summary>
    /// Splits a path into segments.
    /// </summary>
    private static string[] GetSegments(string path)
    {
        if (string.IsNullOrEmpty(path))
            return [];

        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }
}
