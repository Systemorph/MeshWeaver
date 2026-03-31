namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Utility for extracting partition prefixes from paths.
/// Supports both first-segment routing (legacy) and longest-prefix routing.
/// </summary>
public static class PathPartition
{
    /// <summary>
    /// Extracts the first segment from a path.
    /// "Cornerstone/Article" → "Cornerstone"
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

    /// <summary>
    /// Finds the longest matching prefix from a set of registered prefixes.
    /// Tries from longest (full path) down to first segment.
    /// Example: For path "Organization/ACME/Article" with prefixes {"Organization/ACME", "Organization"},
    /// returns "Organization/ACME" (longest match).
    /// </summary>
    /// <param name="path">The full path to route</param>
    /// <param name="registeredPrefixes">Set of registered partition prefixes</param>
    /// <returns>The longest matching prefix, or null if none match</returns>
    public static string? FindLongestMatchingPrefix(string? path, ICollection<string> registeredPrefixes)
    {
        if (string.IsNullOrWhiteSpace(path) || registeredPrefixes.Count == 0)
            return null;

        var normalized = path.Trim('/');
        if (string.IsNullOrEmpty(normalized))
            return null;

        // Try from full path down to first segment
        var current = normalized;
        while (true)
        {
            if (registeredPrefixes.Contains(current))
                return current;

            var lastSlash = current.LastIndexOf('/');
            if (lastSlash < 0)
                break;
            current = current[..lastSlash];
        }

        // Final check: single segment (root partition)
        var firstSlash = normalized.IndexOf('/');
        var firstSegment = firstSlash < 0 ? normalized : normalized[..firstSlash];
        return registeredPrefixes.Contains(firstSegment) ? firstSegment : null;
    }
}
