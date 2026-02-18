using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Layout;

namespace MeshWeaver.Mesh;

/// <summary>
/// Status of a comment in a collaborative document.
/// </summary>
public enum CommentStatus
{
    /// <summary>
    /// Comment is active and awaiting response or action.
    /// </summary>
    Active,

    /// <summary>
    /// Comment has been resolved and addressed.
    /// </summary>
    Resolved
}

/// <summary>
/// Represents a comment on a mesh node.
/// Comments can be nested via ParentCommentId for threading.
/// </summary>
public record Comment : ISatelliteContent
{
    /// <summary>
    /// Unique identifier for the comment.
    /// </summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Path of the node this comment belongs to.
    /// </summary>
    [Browsable(false)]
    public string NodePath { get; init; } = string.Empty;

    /// <summary>
    /// Path of the root document node this comment thread belongs to.
    /// Used for permission checks (edit access on the document).
    /// For top-level comments, this equals NodePath.
    /// For replies, this is the original document path (not the parent comment).
    /// </summary>
    [Browsable(false)]
    public string DocumentPath { get; init; } = string.Empty;

    /// <summary>
    /// Marker ID linking this comment to highlighted text in the document.
    /// Corresponds to the ID in &lt;!--comment:markerId--&gt; markers.
    /// </summary>
    [Browsable(false)]
    public string? MarkerId { get; init; }

    /// <summary>
    /// The original text that was highlighted when the comment was created.
    /// </summary>
    [Browsable(false)]
    public string? HighlightedText { get; init; }

    /// <summary>
    /// Author of the comment (username or display name).
    /// </summary>
    [Browsable(false)]
    public string Author { get; init; } = string.Empty;

    /// <summary>
    /// Comment text content (markdown supported).
    /// </summary>
    [Markdown(EditorHeight = "150px", ShowPreview = false)]
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// When the comment was created.
    /// </summary>
    [Browsable(false)]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional parent comment ID for threaded discussions.
    /// Null for top-level comments.
    /// </summary>
    [Browsable(false)]
    public string? ParentCommentId { get; init; }

    /// <summary>
    /// Status of the comment (Active or Resolved).
    /// </summary>
    [Browsable(false)]
    public CommentStatus Status { get; init; } = CommentStatus.Active;

    /// <summary>
    /// ISatelliteContent: permissions are checked against the document, not the comment itself.
    /// </summary>
    [Browsable(false)]
    public string? PrimaryNodePath => DocumentPath;
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
    /// <summary>
    /// Text was inserted into the document.
    /// </summary>
    Insertion,

    /// <summary>
    /// Text was deleted from the document.
    /// </summary>
    Deletion
}

/// <summary>
/// Status of a tracked change.
/// </summary>
public enum TrackedChangeStatus
{
    /// <summary>
    /// Change is pending review.
    /// </summary>
    Pending,

    /// <summary>
    /// Change has been accepted and applied.
    /// </summary>
    Accepted,

    /// <summary>
    /// Change has been rejected and reverted.
    /// </summary>
    Rejected
}
