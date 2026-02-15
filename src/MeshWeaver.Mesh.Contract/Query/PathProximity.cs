namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Computes a proximity boost for search results based on segment distance
/// between the user's current context path and the result path.
/// </summary>
public static class PathProximity
{
    /// <summary>
    /// Maximum boost value. Chosen so proximity never overpowers a tier difference
    /// (prefix=100 vs contains=50) but meaningfully separates items within the same tier.
    /// </summary>
    public const double MaxBoost = 40;

    /// <summary>
    /// Returns the number of leading path segments that are identical.
    /// </summary>
    public static int LongestCommonPrefixLength(string[] a, string[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return len;
    }

    /// <summary>
    /// Segment distance = (contextSegments - LCP) + (resultSegments - LCP).
    /// </summary>
    public static int SegmentDistance(string[] a, string[] b)
    {
        var lcp = LongestCommonPrefixLength(a, b);
        return (a.Length - lcp) + (b.Length - lcp);
    }

    /// <summary>
    /// Computes a proximity boost for a result path relative to the user's context path.
    /// Returns 0 when contextPath is null or empty (backward compatible).
    /// </summary>
    public static double ComputeBoost(string? contextPath, string? resultPath)
    {
        if (string.IsNullOrEmpty(contextPath))
            return 0;

        var contextSegments = contextPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var resultSegments = string.IsNullOrEmpty(resultPath)
            ? []
            : resultPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var distance = SegmentDistance(contextSegments, resultSegments);
        return MaxBoost / (1 + distance);
    }
}
