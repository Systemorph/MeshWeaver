using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using AnchorMath = MeshWeaver.Markdown.Collaboration.AnchorMath;

namespace MeshWeaver.Graph;

/// <summary>
/// Capture + display logic for text-anchored comments.
///
/// <para>
/// A comment is a satellite at <c>{doc}/_Comment/{id}</c>. The document text stays CLEAN — nothing
/// is woven into it. The comment captures the character range (<see cref="Comment.Start"/>/
/// <see cref="Comment.Length"/>) in the document's clean text at a known <see cref="Comment.Version"/>,
/// plus that text (<see cref="Comment.AnchorText"/>). At display time the effective range is
/// recomputed against the current text via the version delta (<see cref="AnchorMath"/>) and the
/// highlight is injected as a plain <c>&lt;span&gt;</c> for that one render — never persisted.
/// </para>
/// </summary>
public static class CommentRendering
{
    /// <summary>
    /// Maps a selection (start/end fragments, or the full selected text) to a <c>(Start, Length)</c>
    /// range in the document's clean source. Returns <c>(-1, 0)</c> when nothing matches.
    /// </summary>
    public static (int Start, int Length) Capture(
        string cleanContent, string? startFragment, string? endFragment, string? selectedText)
    {
        if (string.IsNullOrEmpty(cleanContent) || string.IsNullOrWhiteSpace(selectedText))
            return (-1, 0);

        var start = !string.IsNullOrEmpty(startFragment)
            ? MarkdownSourceMap.FindFragmentInSource(cleanContent, startFragment)
            : -1;
        var end = !string.IsNullOrEmpty(endFragment)
            ? MarkdownSourceMap.FindFragmentEndInSource(cleanContent, endFragment, start >= 0 ? start : 0)
            : -1;

        if (start < 0 || end < 0 || end <= start)
        {
            var (plain, map) = MarkdownSourceMap.BuildRenderedToSourceMap(cleanContent);
            var idx = plain.IndexOf(selectedText, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return (-1, 0);
            start = idx < map.Length ? map[idx] : cleanContent.Length;
            end = idx + selectedText.Length < map.Length ? map[idx + selectedText.Length] : cleanContent.Length;
        }

        return end > start ? (start, end - start) : (-1, 0);
    }

    /// <summary>
    /// Recomputes the effective range for <paramref name="comment"/> against the current clean text
    /// and version, returning a copy with <see cref="Comment.EffectiveStart"/>/<see cref="Comment.EffectiveEnd"/>/
    /// <see cref="Comment.EffectiveVersion"/> set. Page-level comments (no anchor) resolve to
    /// <c>(-1,-1)</c>.
    /// </summary>
    public static Comment ResolveEffective(Comment comment, string currentClean, long currentVersion)
    {
        currentClean ??= "";

        if (comment.Start < 0 || string.IsNullOrEmpty(comment.HighlightedText))
            return comment with { EffectiveStart = -1, EffectiveEnd = -1, EffectiveVersion = currentVersion };

        int start, end;
        if (comment.AnchorText is not null)
        {
            (start, end) = AnchorMath.Resolve(comment.AnchorText, comment.Start, comment.Length, currentClean);
        }
        else
        {
            // No captured anchor (legacy) — relocate by the highlighted text.
            var idx = currentClean.IndexOf(comment.HighlightedText, StringComparison.Ordinal);
            if (idx < 0)
                return comment with { EffectiveStart = -1, EffectiveEnd = -1, EffectiveVersion = currentVersion };
            (start, end) = (idx, idx + comment.HighlightedText.Length);
        }

        return comment with { EffectiveStart = start, EffectiveEnd = end, EffectiveVersion = currentVersion };
    }

    /// <summary>Resolves a whole set of comments against the current text.</summary>
    public static IReadOnlyList<Comment> ResolveAll(
        IEnumerable<Comment> comments, string currentClean, long currentVersion)
        => comments.Select(c => ResolveEffective(c, currentClean, currentVersion)).ToList();

    /// <summary>
    /// Injects a highlight <c>&lt;span class="comment-highlight"&gt;</c> into <paramref name="cleanContent"/>
    /// at each resolved comment's effective range (comments only — see
    /// <see cref="CollaborativeRenderer.Decorate"/> to overlay comments and tracked changes together).
    /// The spans exist only for this render. Comments must already be resolved.
    /// </summary>
    public static string DecorateInline(string cleanContent, IReadOnlyCollection<Comment> resolved)
        => CollaborativeRenderer.Decorate(cleanContent, resolved, Array.Empty<TrackedChange>());
}
