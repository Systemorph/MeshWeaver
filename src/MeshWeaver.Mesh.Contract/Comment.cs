using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>
/// Status of a comment in a collaborative document.
/// </summary>
public enum CommentStatus
{
    Active,
    Resolved
}

/// <summary>
/// Represents a comment on a mesh node.
/// Comments can be nested via ParentCommentId for threading.
/// </summary>
public record Comment
{
    /// <summary>
    /// Unique identifier for the comment.
    /// </summary>
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Path of the node this comment belongs to.
    /// </summary>
    public string NodePath { get; init; } = string.Empty;

    /// <summary>
    /// Marker ID linking this comment to highlighted text in the document.
    /// Corresponds to the ID in &lt;!--comment:markerId--&gt; markers.
    /// </summary>
    public string? MarkerId { get; init; }

    /// <summary>
    /// The original text that was highlighted when the comment was created.
    /// </summary>
    public string? HighlightedText { get; init; }

    /// <summary>
    /// Author of the comment (username or display name).
    /// </summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>
    /// Comment text content (markdown supported).
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// When the comment was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional parent comment ID for threaded discussions.
    /// Null for top-level comments.
    /// </summary>
    public string? ParentCommentId { get; init; }

    /// <summary>
    /// Status of the comment (Active or Resolved).
    /// </summary>
    public CommentStatus Status { get; init; } = CommentStatus.Active;

    /// <summary>
    /// Replies to this comment (for threaded discussions).
    /// </summary>
    public ImmutableList<Comment> Replies { get; init; } = ImmutableList<Comment>.Empty;
}

/// <summary>
/// Represents a tracked change (insertion or deletion) in a collaborative document.
/// </summary>
public record TrackedChange
{
    /// <summary>
    /// Unique identifier for the change.
    /// </summary>
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Path of the node this change belongs to.
    /// </summary>
    public string NodePath { get; init; } = string.Empty;

    /// <summary>
    /// Marker ID in the document.
    /// </summary>
    public string MarkerId { get; init; } = string.Empty;

    /// <summary>
    /// Type of change (Insertion or Deletion).
    /// </summary>
    public TrackedChangeType ChangeType { get; init; }

    /// <summary>
    /// The text content of the change.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Author of the change.
    /// </summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>
    /// When the change was made.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Status of the change.
    /// </summary>
    public TrackedChangeStatus Status { get; init; } = TrackedChangeStatus.Pending;
}

/// <summary>
/// Type of tracked change.
/// </summary>
public enum TrackedChangeType
{
    Insertion,
    Deletion
}

/// <summary>
/// Status of a tracked change.
/// </summary>
public enum TrackedChangeStatus
{
    Pending,
    Accepted,
    Rejected
}
