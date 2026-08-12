namespace MeshWeaver.Mesh;

/// <summary>
/// The ONE wildcard vocabulary of the query language: <c>*</c>, and only <c>*</c>.
///
/// <para><b>The rule.</b> A <see cref="QueryOperator.Like"/> pattern travelling in a
/// <see cref="ParsedQuery"/> is storage-NEUTRAL and spells its wildcard <c>*</c> — the same
/// character the user typed. SQL's <c>%</c> is a dialect detail that comes into existence inside a
/// SQL generator, at the moment the pattern is bound as a parameter
/// (<c>PostgreSqlSqlGenerator</c> / <c>SnowflakeSqlGenerator</c> both do
/// <c>condition.Value.Replace("*", "%")</c> already), and it never travels back up into the AST.
/// Cosmos, which has no <c>%</c> at all, is the proof that the neutral form is the right one.</para>
///
/// <para><b>Why this type exists (issue #1235).</b> The vocabulary used to FORK. Every
/// <c>Like</c> filter carried <c>*</c> — <c>name:*Laptop*</c>, <c>path:*</c>, the autocomplete
/// prefix filters — except ONE: <see cref="QueryParser"/> rewrote a wildcard NAMESPACE to
/// <c>%</c> (<c>value.Replace("*", "%")</c>), duplicating a translation the SQL generators
/// already do. The in-memory evaluators speak <c>*</c>, so a <c>%</c> pattern reached
/// <c>QueryEvaluator.CompareWildcard</c>, matched neither the leading- nor the trailing-star
/// branch, and fell through to an EQUALITY test against the literal string <c>"%/Source"</c> —
/// which nothing can equal. A wildcard-namespace filter therefore matched NOTHING in memory,
/// silently: no error, no warning, an empty result. That is the same failure shape as #1216, and
/// it sat live under the Postgres fan-out's relevance gate (a NEW row entering such a query could
/// never be seen) and under every non-Postgres provider.</para>
///
/// <para><b>Multi-wildcard is the second half of the same bug.</b> #1232 widened a subtree query
/// to a pair of patterns, the second of which carries TWO wildcards
/// (<c>*/Source/*</c>). Both in-memory matchers split on the FIRST wildcard only, so the second
/// pattern degenerated to <c>EndsWith("/Source/*")</c> and also matched nothing.
/// <see cref="IsMatch"/> therefore implements a REAL glob over any number of wildcards, and is the
/// single implementation both matchers call — a vocabulary with two readers is how the fork
/// happened in the first place.</para>
///
/// <para><b>Deliberately intolerant of <c>%</c>.</b> These matchers do NOT accept <c>%</c> as a
/// wildcard. Being liberal here would re-open exactly the hole it is meant to close: a <c>%</c>
/// leaking back into the AST would keep working in memory while meaning something different in
/// SQL, and the fork would go unnoticed a second time. With <c>*</c> as the only spelling, any
/// re-introduction fails loudly in the cross-path equivalence test instead.</para>
/// </summary>
public static class QueryWildcard
{
    /// <summary>The one wildcard character of the query language.</summary>
    public const char Wildcard = '*';

    /// <summary>True when <paramref name="pattern"/> carries at least one <see cref="Wildcard"/>.</summary>
    public static bool ContainsWildcard(string? pattern) =>
        !string.IsNullOrEmpty(pattern) && pattern.Contains(Wildcard);

    /// <summary>
    /// Removes every wildcard from <paramref name="pattern"/>, leaving the literal text — for
    /// callers that only need to inspect the pattern's LITERAL segments (e.g. resolving which
    /// satellite table a <c>namespace:*/_Thread</c> filter points at) rather than match with it.
    /// <c>%</c> is stripped too: a sanitiser removes metacharacters, it does not decide match
    /// semantics, and a hand-built <see cref="ParsedQuery"/> may still carry a SQL-shaped pattern.
    /// </summary>
    public static string StripWildcards(string? pattern) =>
        string.IsNullOrEmpty(pattern) ? string.Empty : pattern.Replace("*", "").Replace("%", "");

    /// <summary>
    /// Glob-matches <paramref name="value"/> against <paramref name="pattern"/>, case-insensitively.
    /// Each <see cref="Wildcard"/> matches any run of characters (including none); every other
    /// character is literal. A pattern with NO wildcard is an exact (case-insensitive) equality
    /// test — callers that want SQL's "bare pattern means contains" behaviour apply that themselves,
    /// so this method stays a pure glob.
    /// </summary>
    /// <param name="value">The value to test; <see langword="null"/> never matches.</param>
    /// <param name="pattern">The glob pattern.</param>
    public static bool IsMatch(string? value, string? pattern)
    {
        if (value is null || pattern is null)
            return false;

        // Fast path + the no-wildcard contract: exact, case-insensitive equality.
        var first = pattern.IndexOf(Wildcard);
        if (first < 0)
            return value.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        // A leading literal must be a prefix; everything after the LAST wildcard must be a suffix;
        // the literals in between must appear in order, each after the previous one ended. Scanning
        // interior segments greedily (leftmost occurrence) is complete for a `*`-only glob: taking
        // the earliest match can never rule out a later one, because a wildcard absorbs any gap.
        var segments = pattern.Split(Wildcard);

        var head = segments[0];
        if (!value.StartsWith(head, StringComparison.OrdinalIgnoreCase))
            return false;
        var cursor = head.Length;

        var tail = segments[^1];
        // The suffix has to fit in what is left AFTER every interior segment has been consumed, so
        // reserve its length up front and re-check the bound at the end.
        for (var i = 1; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0)
                continue; // `**` — a second wildcard adds nothing.
            var found = value.IndexOf(segment, cursor, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                return false;
            cursor = found + segment.Length;
        }

        if (tail.Length == 0)
            return true; // pattern ends with a wildcard — anything left over is absorbed.

        // The suffix must not overlap what the interior segments already consumed; without this
        // bound `a*b` would wrongly match "ab"→"a" reusing the same characters for both ends.
        return value.Length - tail.Length >= cursor
               && value.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The in-memory reading of <see cref="QueryOperator.Like"/>, mirroring what the SQL generators
    /// emit so both evaluation paths answer identically. A pattern WITH a wildcard is a glob
    /// (<see cref="IsMatch"/>); a pattern WITHOUT one is a CONTAINS test, because that is precisely
    /// what the generators do with it — <c>if (!pattern.Contains('%')) pattern = $"%{pattern}%"</c>
    /// before binding it to <c>ILIKE</c>. The parser never produces a wildcard-free <c>Like</c>
    /// (it only switches to <c>Like</c> when it sees a <c>*</c>), so this arm serves the
    /// hand-built filters — the autocomplete prefix search being the live one.
    /// </summary>
    public static bool IsLikeMatch(string? value, string? pattern)
    {
        if (value is null || pattern is null)
            return false;
        return ContainsWildcard(pattern)
            ? IsMatch(value, pattern)
            : value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
