namespace MeshWeaver.Mesh;

/// <summary>
/// Computes the <b>populated frontier</b> below a base path — the set of nearest real nodes,
/// skipping empty intermediate namespace segments. This is the in-memory counterpart of the
/// Postgres anti-join that backs <see cref="QueryScope.NextLevel"/>.
///
/// <para>For a base path <c>P</c> and a set of candidate node paths, the frontier is every
/// candidate <c>d</c> that is a strict descendant of <c>P</c> for which <b>no other candidate</b>
/// sits strictly between <c>P</c> and <c>d</c>. So if only <c>a/b/node</c> exists (and neither
/// <c>a</c> nor <c>a/b</c> is a real node), <c>Frontier("")</c> returns <c>a/b/node</c> — the empty
/// hops are skipped. If <c>a</c> were also a real node, <c>Frontier("")</c> would return <c>a</c>
/// and <c>Frontier("a")</c> would return <c>a/b/node</c>.</para>
///
/// <para>Comparison is case-insensitive and segment (<c>/</c>) aware — <c>a/bc</c> is NOT a
/// descendant of <c>a/b</c>. Returned paths preserve the original casing of the candidates.</para>
/// </summary>
public static class NamespaceFrontier
{
    /// <summary>
    /// Returns the frontier of <paramref name="candidatePaths"/> relative to
    /// <paramref name="basePath"/> (see type docs). Order is preserved from the input;
    /// callers apply their own sort.
    /// </summary>
    public static IReadOnlyList<string> Frontier(string? basePath, IEnumerable<string?> candidatePaths)
    {
        var baseNorm = Normalize(basePath);

        // Distinct, normalized lookup of all candidates — the suppressor set. Keyed
        // case-insensitively so an ancestor written in different casing still suppresses.
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<(string Original, string Norm)>();
        foreach (var raw in candidatePaths)
        {
            var norm = Normalize(raw);
            if (norm.Length == 0) continue;
            if (active.Add(norm))
                ordered.Add((raw!, norm));
        }

        var result = new List<string>();
        foreach (var (original, norm) in ordered)
        {
            if (IsFrontier(baseNorm, norm, active))
                result.Add(original);
        }
        return result;
    }

    /// <summary>
    /// True when <paramref name="path"/> is on the frontier of <paramref name="basePath"/>:
    /// a strict descendant of the base with no nearer active ancestor between the two.
    /// <paramref name="activePaths"/> must contain normalized (trimmed) paths and use a
    /// case-insensitive comparer.
    /// </summary>
    public static bool IsFrontier(string? basePath, string? path, ISet<string> activePaths)
    {
        var baseNorm = Normalize(basePath);
        var norm = Normalize(path);
        if (!IsStrictDescendant(baseNorm, norm))
            return false;

        // Walk every ancestor prefix of `path` that lies strictly between base and path.
        // If any such prefix is itself an active node, it is nearer → `path` is suppressed.
        var segments = norm.Split('/');
        var baseDepth = baseNorm.Length == 0 ? 0 : baseNorm.Split('/').Length;
        for (var depth = baseDepth + 1; depth < segments.Length; depth++)
        {
            var ancestor = string.Join('/', segments.Take(depth));
            if (activePaths.Contains(ancestor))
                return false;
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a strict descendant of <paramref name="baseNorm"/>
    /// (deeper, sharing the base as a segment-boundary prefix). Root base (<c>""</c>) is an
    /// ancestor of every non-empty path.
    /// </summary>
    private static bool IsStrictDescendant(string baseNorm, string path)
    {
        if (path.Length == 0) return false;
        if (baseNorm.Length == 0) return true;
        return path.Length > baseNorm.Length
               && path.StartsWith(baseNorm, StringComparison.OrdinalIgnoreCase)
               && path[baseNorm.Length] == '/';
    }

    private static string Normalize(string? path) => path?.Trim('/') ?? "";
}
