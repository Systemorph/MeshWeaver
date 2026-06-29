#nullable enable

using System;

namespace MeshWeaver.Data.Completion;

/// <summary>
/// The ranking law for <c>@</c>-autocomplete suggestions, as one composite score.
///
/// <para><b>Tiers (highest weight first):</b></para>
/// <list type="number">
///   <item><b>Path occurrence</b> — does the typed token occur in the node's PATH, and how
///         strongly (exact id &gt; exact segment &gt; prefix &gt; multiple occurrences &gt; single
///         occurrence). This weighs the MOST: any item whose path matches outranks any item that
///         matches only on its title.</item>
///   <item><b>Title</b> — does the token match the node's display title/name (exact &gt; prefix
///         &gt; contains). Breaks ties between items with equal path strength.</item>
///   <item><b>Relevance</b> — the underlying relevance rank (vector-index order for subtree
///         results, provider score otherwise), as the lowest-order tiebreaker.</item>
/// </list>
///
/// <para>The three tiers are packed into the <c>0..9990</c> band that the chat completion pipeline
/// already sorts on (<c>AutocompleteItem.Priority</c> → the UI's
/// <c>9999 - min(categoryPriority + Priority, 9999)</c> sort key), so applying this score needs no
/// change to the Monaco sort key: emit the blended <c>@</c> items with <c>categoryPriority = 0</c>
/// and <c>Priority = Score(...)</c>, and they rank by path &gt; title &gt; relevance.</para>
/// </summary>
public static class AutocompleteRelevance
{
    /// <summary>Path tier weight — dominant (a path match always outranks a title-only match).</summary>
    public const int PathWeight = 1000;
    /// <summary>Title tier weight — second.</summary>
    public const int TitleWeight = 100;
    /// <summary>Relevance tier weight — lowest-order tiebreaker.</summary>
    public const int RelevanceWeight = 10;

    /// <summary>
    /// Composite ranking score in <c>0..9990</c>.
    /// </summary>
    /// <param name="queryToken">The bare text typed after <c>@</c> (no prefixes/slashes).</param>
    /// <param name="path">The candidate node's full path.</param>
    /// <param name="title">The candidate node's display title / name / id.</param>
    /// <param name="relevanceRank">A <c>0..9</c> underlying relevance rank (higher = more relevant);
    /// e.g. derived from vector-search position or a coarse provider band. Clamped to <c>0..9</c>.</param>
    public static int Score(string? queryToken, string? path, string? title, int relevanceRank)
        => PathTier(queryToken, path) * PathWeight
         + TitleTier(queryToken, title) * TitleWeight
         + Math.Clamp(relevanceRank, 0, 9) * RelevanceWeight;

    /// <summary>
    /// Path-occurrence tier <c>0..9</c>. Exact id (last segment) is strongest; an exact non-last
    /// segment next; prefix matches next; then substring matches, where MORE occurrences rank above
    /// a single occurrence (honouring "occurrence in path weighs the most").
    /// </summary>
    public static int PathTier(string? queryToken, string? path)
    {
        if (string.IsNullOrEmpty(queryToken) || string.IsNullOrEmpty(path))
            return 0;

        var q = queryToken.Trim().ToLowerInvariant();
        var p = path.ToLowerInvariant();
        if (q.Length == 0) return 0;

        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return 0;

        var last = segments[^1];
        if (last == q) return 9;                               // exact node id

        var anyExactSegment = false;
        var anyPrefixSegment = false;
        foreach (var seg in segments)
        {
            if (seg == q) anyExactSegment = true;
            if (seg.StartsWith(q, StringComparison.Ordinal)) anyPrefixSegment = true;
        }
        if (anyExactSegment) return 8;                         // exact ancestor segment
        if (last.StartsWith(q, StringComparison.Ordinal)) return 7; // id prefix
        if (anyPrefixSegment) return 6;                        // ancestor-segment prefix

        var occurrences = CountOccurrences(p, q);
        if (occurrences >= 2) return 5;                        // multiple substring occurrences
        if (occurrences == 1) return 4;                        // single substring occurrence
        return 0;
    }

    /// <summary>
    /// Title tier <c>0..9</c>: exact match (9) &gt; prefix (7) &gt; substring (4) &gt; none (0).
    /// </summary>
    public static int TitleTier(string? queryToken, string? title)
    {
        if (string.IsNullOrEmpty(queryToken) || string.IsNullOrEmpty(title))
            return 0;

        var q = queryToken.Trim().ToLowerInvariant();
        var t = title.ToLowerInvariant();
        if (q.Length == 0) return 0;

        if (t == q) return 9;
        if (t.StartsWith(q, StringComparison.Ordinal)) return 7;
        if (t.Contains(q, StringComparison.Ordinal)) return 4;
        return 0;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
