namespace MeshWeaver.Layout;

/// <summary>
/// Renders a run of plain text with the search-matched passages wrapped in <c>&lt;mark&gt;</c>.
/// The matches are computed from <see cref="Terms"/> at render time (no stored offsets) — the
/// chunk text is verbatim from the extracted document text, so the free-text query terms can be
/// located in the excerpt, the chunk-block reader, and the rendered original alike.
///
/// <para>This is the framework-correct alternative to building an HTML string with <c>&lt;mark&gt;</c>
/// tags: the Blazor view (<c>HighlightView</c>) renders the segments as real DOM via a
/// <c>RenderFragment</c>, so nothing is hand-interpolated into markup. Reused by the global search
/// content rows, the <c>Document</c> Blocks reader, and the original-file viewer fallback.</para>
/// </summary>
/// <param name="Text">The full text to display (an excerpt, a chunk, or a passage).</param>
/// <param name="Terms">
/// The free-text terms to highlight, space-separated. Mesh-search grammar tokens
/// (<c>key:value</c>) are ignored — pass the raw query and let <see cref="FreeTextTerms"/> strip them,
/// or pass already-extracted terms.
/// </param>
public record HighlightControl(object Text, object? Terms = null)
    : UiControl<HighlightControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>Returns a copy whose highlight terms are <paramref name="terms"/> (space-separated).</summary>
    /// <param name="terms">The free-text terms to mark in <see cref="Text"/>.</param>
    public HighlightControl WithTerms(string terms) => this with { Terms = terms };

    /// <summary>The shortest term length that is highlighted — single characters are never marked.</summary>
    private const int MinTermLength = 2;

    /// <summary>
    /// Extracts the free-text terms from a mesh-search query: splits on whitespace and drops the
    /// grammar tokens (anything containing <c>:</c>, e.g. <c>namespace:X</c>, <c>scope:subtree</c>,
    /// <c>nodeType:Story</c>) and a leading <c>@</c> path token. The remainder is what a user reads as
    /// "the words I searched for" — the terms worth highlighting. Distinct, order-preserving.
    /// </summary>
    public static IReadOnlyList<string> FreeTextTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<string>();
        foreach (var raw in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // Grammar token (key:value) or a path reference — not a free-text word.
            if (raw.Contains(':') || raw.StartsWith('@'))
                continue;
            var term = raw.Trim('"', '\'', '(', ')', ',', '.', ';');
            if (term.Length < MinTermLength)
                continue;
            if (seen.Add(term))
                terms.Add(term);
        }
        return terms;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into alternating non-match / match segments for the given
    /// <paramref name="terms"/> (case-insensitive). Overlapping or adjacent matches are merged. When
    /// there are no terms or no matches, the result is a single non-match segment carrying the whole
    /// text — so the caller can always render the segments uniformly.
    /// </summary>
    public static IReadOnlyList<HighlightSegment> Segment(string? text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<HighlightSegment>();
        if (terms.Count == 0)
            return new[] { new HighlightSegment(text, false) };

        // Collect every match range across all terms.
        var ranges = new List<(int Start, int End)>();
        foreach (var term in terms)
        {
            if (string.IsNullOrEmpty(term))
                continue;
            var from = 0;
            while (from <= text.Length - term.Length)
            {
                var idx = text.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    break;
                ranges.Add((idx, idx + term.Length));
                from = idx + term.Length;
            }
        }

        if (ranges.Count == 0)
            return new[] { new HighlightSegment(text, false) };

        // Merge overlapping / touching ranges so a marked span is emitted once.
        ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var merged = new List<(int Start, int End)>();
        var (curStart, curEnd) = ranges[0];
        for (var i = 1; i < ranges.Count; i++)
        {
            var (s, e) = ranges[i];
            if (s <= curEnd)
                curEnd = Math.Max(curEnd, e);
            else
            {
                merged.Add((curStart, curEnd));
                (curStart, curEnd) = (s, e);
            }
        }
        merged.Add((curStart, curEnd));

        // Emit alternating non-match / match segments.
        var segments = new List<HighlightSegment>();
        var pos = 0;
        foreach (var (s, e) in merged)
        {
            if (s > pos)
                segments.Add(new HighlightSegment(text[pos..s], false));
            segments.Add(new HighlightSegment(text[s..e], true));
            pos = e;
        }
        if (pos < text.Length)
            segments.Add(new HighlightSegment(text[pos..], false));
        return segments;
    }
}

/// <summary>One run of text within a <see cref="HighlightControl"/>: marked (a search match) or plain.</summary>
/// <param name="Text">The literal text of this run.</param>
/// <param name="IsMatch">When <c>true</c>, the run is a search match and is rendered inside <c>&lt;mark&gt;</c>.</param>
public readonly record struct HighlightSegment(string Text, bool IsMatch);
