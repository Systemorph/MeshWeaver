namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Utility for extracting the first path segment used for partition routing.
/// </summary>
public static class PathPartition
{
    /// <summary>
    /// Extracts the first segment from a path.
    /// "Cornerstone/Article" → "ACME"
    /// "ACME" → "ACME" (root node)
    /// "" or null → null (root level, no partition)
    /// </summary>
    public static string? GetFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var normalized = path.Trim('/').Trim();
        if (string.IsNullOrEmpty(normalized))
            return null;
        var slashIndex = normalized.IndexOf('/');
        return slashIndex < 0 ? normalized : normalized[..slashIndex];
    }
}
