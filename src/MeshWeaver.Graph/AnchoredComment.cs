using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Builds the <see cref="Comment"/> satellite node for a comment created from a TEXT SELECTION —
/// the one place that turns "the reader selected this" into an anchored comment, shared by every
/// surface that offers the affordance (the markdown view, and <c>CommentableControl</c> for any
/// other rendered content).
///
/// <para>The anchor is captured as a <c>(Start, Length)</c> range in the node's plain source text
/// plus the text it was captured against. The stored content is never touched: the highlight is
/// RECOMPUTED from that capture at display time (<see cref="CommentRendering.ResolveAll"/>), so an
/// anchored comment survives later edits and works for readers who may comment but not edit.</para>
/// </summary>
public static class AnchoredComment
{
    /// <summary>
    /// Builds the satellite node for a selection comment on <paramref name="nodePath"/>.
    /// <para>A selection that cannot be located in <paramref name="anchorText"/> — a selection in
    /// rendered chrome that has no source counterpart, or one spanning stripped markers — is NOT
    /// an error: the comment is created UNANCHORED (no marker, no range), exactly as the markdown
    /// view has always degraded. It still belongs to the node and still shows in its Comments
    /// area; it simply has no inline highlight.</para>
    /// </summary>
    /// <param name="nodePath">The node being commented on — becomes the satellite's MainNode.</param>
    /// <param name="anchorText">The node's plain SOURCE text the selection is captured against.</param>
    /// <param name="selectedText">The text the reader selected, as rendered.</param>
    /// <param name="startFragment">Leading words of the selection, for locating its start.</param>
    /// <param name="endFragment">Trailing words of the selection, for locating its end.</param>
    /// <param name="author">Display name of the commenting user.</param>
    /// <param name="commentText">The comment body.</param>
    /// <param name="documentVersion">Node version the capture was taken against.</param>
    /// <returns>The satellite <see cref="MeshNode"/> to create.</returns>
    public static MeshNode Build(
        string nodePath,
        string? anchorText,
        string selectedText,
        string? startFragment,
        string? endFragment,
        string? author,
        string? commentText,
        long documentVersion)
    {
        var clean = anchorText ?? string.Empty;
        var (start, length) = CommentRendering.Capture(clean, startFragment, endFragment, selectedText);
        var anchored = start >= 0 && length > 0;

        var markerId = Guid.NewGuid().ToString("N")[..8];
        var comment = new Comment
        {
            Id = markerId,
            PrimaryNodePath = nodePath,
            MarkerId = anchored ? markerId : null,
            HighlightedText = anchored ? selectedText : null,
            Author = author ?? string.Empty,
            Text = commentText ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = CommentStatus.Active,
            Version = anchored ? documentVersion : 0,
            Start = anchored ? start : -1,
            Length = anchored ? length : 0,
            AnchorText = anchored ? clean : null,
        };

        return new MeshNode(comment.Id, $"{nodePath}/{CommentsExtensions.CommentPartition}")
        {
            Name = string.IsNullOrEmpty(comment.Author) ? "Comment" : $"Comment by {comment.Author}",
            NodeType = CommentNodeType.NodeType,
            MainNode = nodePath,
            Content = comment,
        };
    }
}
