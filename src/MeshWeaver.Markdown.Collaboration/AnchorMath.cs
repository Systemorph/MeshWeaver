namespace MeshWeaver.Markdown.Collaboration;

/// <summary>
/// Version-delta position engine for anchored annotations (comments and tracked changes).
///
/// <para>
/// An annotation captures a character range <c>[start, start+length)</c> in the document text at a
/// known version, together with that text (the "anchor"). The document text is kept CLEAN — nothing
/// is woven into it. When the document later moves on, the annotation's range is re-derived by
/// diffing the anchor text against the current text and mapping the offsets through that diff
/// (the classic <c>diff_xIndex</c> operation). This is fully deterministic and string-only, so it
/// is exhaustively unit-testable and free of any rendered-vs-source / browser-vs-server ambiguity.
/// </para>
/// </summary>
public static class AnchorMath
{
    /// <summary>The kind of a single diff segment.</summary>
    public enum Op
    {
        /// <summary>Characters common to both texts.</summary>
        Equal,
        /// <summary>Characters present in the old text only (removed).</summary>
        Delete,
        /// <summary>Characters present in the new text only (added).</summary>
        Insert
    }

    /// <summary>A run of <paramref name="Length"/> characters of a single <paramref name="Op"/>.</summary>
    public readonly record struct Segment(Op Op, int Length);

    /// <summary>
    /// Above this many DP cells the middle diff falls back to a coarse "delete all / insert all"
    /// segment pair (still correct for position mapping, just less precise). Keeps the worst case
    /// bounded for large documents; real edits leave a small middle after prefix/suffix trimming.
    /// </summary>
    private const long MaxCells = 6_000_000;

    /// <summary>
    /// Character-level diff turning <paramref name="from"/> into <paramref name="to"/>. Trims the
    /// common prefix and suffix, then runs an LCS diff over the changed middle.
    /// </summary>
    public static IReadOnlyList<Segment> Diff(string? from, string? to)
    {
        from ??= "";
        to ??= "";

        var max = Math.Min(from.Length, to.Length);
        var prefix = 0;
        while (prefix < max && from[prefix] == to[prefix])
            prefix++;

        var suffix = 0;
        while (suffix < from.Length - prefix && suffix < to.Length - prefix
               && from[from.Length - 1 - suffix] == to[to.Length - 1 - suffix])
            suffix++;

        var segments = new List<Segment>();
        if (prefix > 0)
            segments.Add(new Segment(Op.Equal, prefix));

        DiffMiddle(
            from.AsSpan(prefix, from.Length - prefix - suffix),
            to.AsSpan(prefix, to.Length - prefix - suffix),
            segments);

        if (suffix > 0)
            segments.Add(new Segment(Op.Equal, suffix));

        return Coalesce(segments);
    }

    private static void DiffMiddle(ReadOnlySpan<char> a, ReadOnlySpan<char> b, List<Segment> output)
    {
        if (a.Length == 0 && b.Length == 0)
            return;
        if (a.Length == 0)
        {
            output.Add(new Segment(Op.Insert, b.Length));
            return;
        }
        if (b.Length == 0)
        {
            output.Add(new Segment(Op.Delete, a.Length));
            return;
        }
        if ((long)a.Length * b.Length > MaxCells)
        {
            // Coarse fallback: delete the whole middle, insert the whole middle. Positions inside
            // collapse to the edit start — acceptable for pathologically large single edits.
            output.Add(new Segment(Op.Delete, a.Length));
            output.Add(new Segment(Op.Insert, b.Length));
            return;
        }

        // Longest-common-subsequence lengths, computed bottom-up.
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        // Walk the table forward, emitting one op per step; coalesced afterwards.
        var ops = new List<Op>(a.Length + b.Length);
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                ops.Add(Op.Equal);
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                ops.Add(Op.Delete);
                x++;
            }
            else
            {
                ops.Add(Op.Insert);
                y++;
            }
        }
        while (x++ < a.Length) ops.Add(Op.Delete);
        while (y++ < b.Length) ops.Add(Op.Insert);

        // Coalesce the per-char ops into runs.
        var runOp = ops[0];
        var runLen = 0;
        foreach (var op in ops)
        {
            if (op == runOp)
            {
                runLen++;
            }
            else
            {
                output.Add(new Segment(runOp, runLen));
                runOp = op;
                runLen = 1;
            }
        }
        output.Add(new Segment(runOp, runLen));
    }

    private static IReadOnlyList<Segment> Coalesce(List<Segment> segments)
    {
        if (segments.Count <= 1)
            return segments;

        var result = new List<Segment>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                continue;
            if (result.Count > 0 && result[^1].Op == segment.Op)
                result[^1] = result[^1] with { Length = result[^1].Length + segment.Length };
            else
                result.Add(segment);
        }
        return result;
    }

    /// <summary>
    /// Maps <paramref name="indexInFrom"/> (an offset into the old text) to the corresponding offset
    /// into the new text, given the <paramref name="diff"/> that turns old into new. A position that
    /// lands inside deleted text collapses to the start of that deletion in the new text
    /// (<c>diff_xIndex</c> semantics).
    /// <para>
    /// At an exact boundary that has an insertion on it, <paramref name="biasLeft"/> controls which
    /// side wins: a range <b>start</b> sticks to the right (<c>false</c>, after the insertion); a
    /// range <b>end</b> sticks to the left (<c>true</c>, before the insertion). This keeps text
    /// inserted just before/after a highlight from being pulled into it.
    /// </para>
    /// </summary>
    public static int MapIndex(IReadOnlyList<Segment> diff, int indexInFrom, bool biasLeft = false)
    {
        if (indexInFrom < 0)
            indexInFrom = 0;

        int fromPos = 0, toPos = 0;
        foreach (var segment in diff)
        {
            switch (segment.Op)
            {
                case Op.Equal:
                    var bound = fromPos + segment.Length;
                    if (indexInFrom < bound || (biasLeft && indexInFrom == bound))
                        return toPos + (indexInFrom - fromPos);
                    fromPos = bound;
                    toPos += segment.Length;
                    break;
                case Op.Delete:
                    if (indexInFrom < fromPos + segment.Length)
                        return toPos;
                    fromPos += segment.Length;
                    break;
                case Op.Insert:
                    if (biasLeft && indexInFrom <= fromPos)
                        return toPos;
                    toPos += segment.Length;
                    break;
            }
        }
        return toPos;
    }

    /// <summary>
    /// Re-anchors a range captured against <paramref name="anchor"/> onto <paramref name="current"/>.
    /// Returns the effective <c>[Start, End)</c> in <paramref name="current"/>.
    /// </summary>
    public static (int Start, int End) Resolve(string? anchor, int start, int length, string? current)
    {
        anchor ??= "";
        current ??= "";

        start = Math.Clamp(start, 0, anchor.Length);
        var end = Math.Clamp(start + Math.Max(0, length), start, anchor.Length);

        if (string.Equals(anchor, current, StringComparison.Ordinal))
            return (Math.Min(start, current.Length), Math.Min(end, current.Length));

        var diff = Diff(anchor, current);
        var effectiveStart = MapIndex(diff, start);
        var effectiveEnd = MapIndex(diff, end, biasLeft: true);
        if (effectiveEnd < effectiveStart)
            effectiveEnd = effectiveStart;
        return (effectiveStart, effectiveEnd);
    }
}
