using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Collaboration;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Anchoring + rendering helpers for text-range comments.
///
/// <para>
/// Comments are satellites at <c>{doc}/_Comment/{id}</c>. Their inline highlight is NOT stored
/// in the document text; instead each <see cref="Comment"/> records the rendered-text range it was
/// anchored to (<see cref="Comment.FromPosition"/>/<see cref="Comment.ToPosition"/>) together with
/// the document <see cref="MeshNode.Version"/> those offsets were computed against, plus the
/// <see cref="Comment.HighlightedText"/> itself. At render time the highlight is recomputed:
/// </para>
/// <list type="bullet">
///   <item>if the document is still at the comment's <see cref="Comment.Version"/> and the stored
///   range still spells the <see cref="Comment.HighlightedText"/>, the stored offsets are used
///   verbatim;</item>
///   <item>otherwise — the text moved on, or the comment predates this mechanism — the comment is
///   re-anchored by locating <see cref="Comment.HighlightedText"/> in the current rendered text,
///   nearest the old offset.</item>
/// </list>
/// <para>
/// This keeps comments decoupled from the document: commenting never mutates (nor needs write
/// access to) the document, and edits above a comment don't strand its highlight. There is no
/// persisted per-version text store, so re-anchoring keys off the stored anchor text — the
/// standard, robust equivalent of replaying an edit history.
/// </para>
/// </summary>
public static class CommentAnchoring
{
    /// <summary>
    /// Finds the <c>[from,to)</c> range, in the rendered plain text of <paramref name="cleanMarkdown"/>,
    /// that a new selection refers to. Tries the start/end fragments first (robust to the rendered
    /// vs. source mismatch caused by markdown syntax), then falls back to the full selected text.
    /// Returns <c>(-1,-1)</c> when nothing matches.
    /// </summary>
    public static (int From, int To) FindRenderedRange(
        string cleanMarkdown, string? startFragment, string? endFragment, string? selectedText)
    {
        if (string.IsNullOrEmpty(cleanMarkdown) || string.IsNullOrWhiteSpace(selectedText))
            return (-1, -1);
        var (plain, _) = MarkdownSourceMap.BuildRenderedToSourceMap(cleanMarkdown);
        return FindRenderedRangeInPlainText(plain, startFragment, endFragment, selectedText);
    }

    /// <summary>
    /// Fragment/text matching over already-rendered plain text. Exposed for callers that have
    /// already built the rendered text (avoids re-parsing the markdown).
    /// </summary>
    public static (int From, int To) FindRenderedRangeInPlainText(
        string plainText, string? startFragment, string? endFragment, string? selectedText)
    {
        if (string.IsNullOrEmpty(plainText) || string.IsNullOrWhiteSpace(selectedText))
            return (-1, -1);

        var from = !string.IsNullOrEmpty(startFragment)
            ? plainText.IndexOf(startFragment, StringComparison.OrdinalIgnoreCase)
            : -1;
        if (from < 0)
            from = plainText.IndexOf(selectedText, StringComparison.OrdinalIgnoreCase);
        if (from < 0)
            return (-1, -1);

        var to = -1;
        if (!string.IsNullOrEmpty(endFragment))
        {
            var endIdx = plainText.IndexOf(endFragment, from, StringComparison.OrdinalIgnoreCase);
            if (endIdx >= 0)
                to = endIdx + endFragment.Length;
        }
        if (to < 0)
            to = from + selectedText.Length;
        to = Math.Min(to, plainText.Length);
        return to > from ? (from, to) : (-1, -1);
    }

    /// <summary>
    /// Re-anchors <paramref name="anchorText"/> in <paramref name="plainText"/> by choosing the
    /// occurrence whose start is nearest <paramref name="hintFrom"/>. Returns <c>-1</c> when the
    /// text no longer appears (the comment is orphaned).
    /// </summary>
    public static int ReAnchor(string plainText, string anchorText, int hintFrom)
    {
        if (string.IsNullOrEmpty(plainText) || string.IsNullOrEmpty(anchorText))
            return -1;

        var best = -1;
        var bestDistance = int.MaxValue;
        var idx = plainText.IndexOf(anchorText, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var distance = Math.Abs(idx - hintFrom);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = idx;
            }
            idx = plainText.IndexOf(anchorText, idx + 1, StringComparison.OrdinalIgnoreCase);
        }
        return best;
    }

    /// <summary>
    /// Resolves the current rendered <c>[from,to)</c> range for <paramref name="comment"/> against
    /// the current <paramref name="plainText"/> and document <paramref name="docVersion"/>. Returns
    /// <c>null</c> when the comment has no inline anchor (page-level) or its text is gone.
    /// </summary>
    public static (int From, int To)? ResolveRenderedRange(Comment comment, string plainText, long docVersion)
    {
        var anchor = comment.HighlightedText;
        if (string.IsNullOrEmpty(anchor))
            return null;

        var from = comment.FromPosition;
        var to = comment.ToPosition;

        // Stored offsets are authoritative only while the text hasn't moved on.
        if (comment.Version == docVersion
            && from >= 0 && to <= plainText.Length && to > from
            && string.Equals(plainText.Substring(from, to - from), anchor, StringComparison.Ordinal))
            return (from, to);

        // Text moved on (or a legacy comment with no stored offsets) — recompute from the content.
        var anchored = ReAnchor(plainText, anchor, from < 0 ? 0 : from);
        if (anchored < 0)
            return null;
        return (anchored, Math.Min(anchored + anchor.Length, plainText.Length));
    }

    /// <summary>
    /// Produces a copy of <paramref name="rawContent"/> with comment markers injected for each
    /// supplied <paramref name="comments"/>, ready for the standard annotation rendering pipeline.
    /// The document text itself is never persisted with these markers — they exist only for this
    /// render. Comments with no inline anchor (page-level), no <see cref="Comment.MarkerId"/>, or
    /// whose text is gone are skipped.
    /// </summary>
    public static string DecorateWithComments(
        string rawContent, IReadOnlyCollection<Comment> comments, long docVersion)
    {
        if (string.IsNullOrEmpty(rawContent) || comments.Count == 0)
            return rawContent ?? "";

        var anchored = comments.Where(c => !string.IsNullOrEmpty(c.MarkerId)).ToArray();
        if (anchored.Length == 0)
            return rawContent;

        // Drop any embedded copy of THESE comments — legacy docs where the old handler wrote both a
        // marker and a satellite — so we don't double-render. Embedded markers with no satellite
        // (e.g. demo content) and all track-change markers are left untouched.
        var baseContent = rawContent;
        foreach (var id in anchored.Select(c => c.MarkerId!).Distinct())
            baseContent = MarkdownAnnotationParser.RemoveMarkers(baseContent, id);

        var clean = MarkdownAnnotationParser.StripAllMarkers(baseContent);
        var (plain, renderedToSource) = MarkdownSourceMap.BuildRenderedToSourceMap(clean);
        var cleanToBase = MarkdownAnnotationParser.BuildCleanToAnnotatedMap(baseContent);

        // Resolve each comment to a base-content [from,to) range.
        var resolved = new List<(int From, int To, Comment Comment)>();
        foreach (var comment in anchored)
        {
            var rendered = ResolveRenderedRange(comment, plain, docVersion);
            if (rendered is not { } range)
                continue;

            var srcFrom = range.From < renderedToSource.Length ? renderedToSource[range.From] : clean.Length;
            var srcTo = range.To < renderedToSource.Length ? renderedToSource[range.To] : clean.Length;
            var baseFrom = srcFrom < cleanToBase.Length ? cleanToBase[srcFrom] : baseContent.Length;
            var baseTo = srcTo < cleanToBase.Length ? cleanToBase[srcTo] : baseContent.Length;
            if (baseTo < baseFrom)
                continue;
            resolved.Add((baseFrom, baseTo, comment));
        }

        // Inject from the end so earlier offsets stay valid.
        var decorated = baseContent;
        foreach (var (from, to, comment) in resolved.OrderByDescending(r => r.From))
        {
            var safeTo = Math.Clamp(to, 0, decorated.Length);
            var safeFrom = Math.Clamp(from, 0, safeTo);
            var meta = string.IsNullOrEmpty(comment.Author)
                ? ""
                : $":{comment.Author}:{comment.CreatedAt.ToLocalTime():MMM d}";
            var open = $"<!--comment:{comment.MarkerId}{meta}-->";
            var close = $"<!--/comment:{comment.MarkerId}-->";
            decorated = decorated.Insert(safeTo, close).Insert(safeFrom, open);
        }
        return decorated;
    }
}
