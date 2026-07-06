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

            // Next populated level (frontier). The frontier can change on ANY subtree change —
            // a new nearer node collapses it, a delete reopens it — so notify on the whole
            // subtree and let the synced query re-run + recompute the frontier (cheap, correct).
            QueryScope.NextLevel => IsDescendant(normalizedChanged, normalizedBase),

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

    /// <summary>
    /// Relevance test for a live synced query: a CRUD change at <paramref name="changedPath"/> should
    /// trigger a re-query when EITHER its path matches the query's path + <paramref name="scope"/>
    /// (<see cref="ShouldNotify"/>), OR the changed node's NAMESPACE matches one of the query's namespace
    /// filters (<paramref name="queryNamespaces"/>).
    /// <para>The path-only <see cref="ShouldNotify"/> is blind to NAMESPACE-filtered queries: the
    /// "open threads" catalog queries <c>namespace:{owner}/*_Thread</c> with NO <c>path:</c> term, so its
    /// base path is empty and ShouldNotify degrades to "direct children of root". A thread deleted three
    /// levels deep (<c>{owner}/_Thread/{id}</c>) is then judged out of scope → no re-query → the catalog
    /// never refreshes on delete. Matching the changed node's namespace (the path minus its last segment)
    /// against the query namespaces closes that gap for create/update/delete alike. Broadening relevance
    /// is correctness-safe: the worst case is a redundant re-query, never a missed change.</para>
    /// </summary>
    public static bool ShouldNotifyForQuery(
        string changedPath, string queryBasePath, QueryScope scope, IReadOnlyList<string>? queryNamespaces)
        => ShouldNotify(changedPath, queryBasePath, scope)
           || NamespaceInScope(NamespaceOf(changedPath), queryNamespaces);

    /// <summary>The namespace a node at <paramref name="path"/> lives in — the path minus its last segment
    /// (a thread <c>{owner}/_Thread/{id}</c> → <c>{owner}/_Thread</c>). Empty for a top-level node.</summary>
    public static string NamespaceOf(string path)
    {
        var normalized = NormalizePath(path);
        var i = normalized.LastIndexOf('/');
        return i > 0 ? normalized[..i] : "";
    }

    /// <summary>
    /// True when <paramref name="ns"/> matches any of <paramref name="patterns"/>. A pattern may carry a
    /// single <c>*</c> glob (<c>{owner}/*_Thread</c> → starts-with the pre-<c>*</c> part AND ends-with the
    /// post-<c>*</c> part); a pattern without <c>*</c> matches when <paramref name="ns"/> equals it OR is a
    /// sub-namespace under it (robust to whether the parser hands back the glob pattern or its base).
    /// </summary>
    public static bool NamespaceInScope(string ns, IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0 || string.IsNullOrEmpty(ns))
            return false;
        var value = NormalizePath(ns);
        for (var i = 0; i < patterns.Count; i++)
            if (GlobMatch(value, NormalizePath(patterns[i])))
                return true;
        return false;
    }

    private static bool GlobMatch(string value, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;
        // The query parser emits a namespace wildcard as SQL-LIKE '%' (it rewrites the user's '*' →
        // '%'); accept either. A single wildcard splits the pattern into a prefix + suffix (e.g.
        // "{owner}/%_Thread" → "{owner}/" … "_Thread"); the surrounding literals (incl. the satellite
        // "_" in "_Thread") are matched verbatim, which is what real satellite namespaces need.
        var star = pattern.IndexOfAny(['*', '%']);
        if (star >= 0)
        {
            var prefix = pattern[..star];
            var suffix = pattern[(star + 1)..];
            return value.Length >= prefix.Length + suffix.Length
                   && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                   && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }
        // No glob: exact namespace, or a sub-namespace under it.
        return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase)
               || value.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase);
    }
}
