using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Markdown.Collaboration;

/// <summary>
/// A comment anchored to a text range in a markdown document.
/// The range is tracked via embedded markers in the markdown content:
/// &lt;!--comment:MarkerId--&gt;highlighted text&lt;!--/comment:MarkerId--&gt;
/// </summary>
public record RangeComment
{
    /// <summary>
    /// Unique identifier for this comment.
    /// </summary>
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The document this comment belongs to.
    /// </summary>
    public string DocumentId { get; init; } = string.Empty;

    /// <summary>
    /// The marker ID embedded in the markdown content.
    /// Links to &lt;!--comment:MarkerId--&gt;...&lt;!--/comment:MarkerId--&gt;
    /// </summary>
    public string MarkerId { get; init; } = string.Empty;

    /// <summary>
    /// Author of the comment.
    /// </summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>
    /// The comment text content.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// When the comment was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the comment was resolved, if applicable.
    /// </summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>
    /// Who resolved the comment.
    /// </summary>
    public string? ResolvedBy { get; init; }

    /// <summary>
    /// The original text that was selected when the comment was created.
    /// Used for display and fuzzy matching if markers become invalid.
    /// </summary>
    public string OriginalSelectedText { get; init; } = string.Empty;

    /// <summary>
    /// The current state of the comment.
    /// </summary>
    public CommentState State { get; init; } = CommentState.Active;

    /// <summary>
    /// Replies to this comment, forming a thread.
    /// </summary>
    public ImmutableList<CommentReply> Replies { get; init; } = ImmutableList<CommentReply>.Empty;
}

/// <summary>
/// A reply to a comment.
/// </summary>
public record CommentReply(
    string Id,
    string Author,
    string Text,
    DateTimeOffset CreatedAt
)
{
    public CommentReply() : this(Guid.NewGuid().ToString(), string.Empty, string.Empty, DateTimeOffset.UtcNow) { }
}

/// <summary>
/// Lifecycle states for a comment.
/// </summary>
public enum CommentState
{
    /// <summary>
    /// Comment is active and visible.
    /// </summary>
    Active,

    /// <summary>
    /// Comment has been resolved/addressed.
    /// </summary>
    Resolved,

    /// <summary>
    /// Comment has been deleted.
    /// </summary>
    Deleted,

    /// <summary>
    /// Comment's text range no longer exists in the document (orphaned).
    /// </summary>
    Orphaned
}
