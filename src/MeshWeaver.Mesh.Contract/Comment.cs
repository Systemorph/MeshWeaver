using System.Collections.Immutable;
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
/// Comments are nested via MeshNode path hierarchy for threading.
/// </summary>
public record Comment
{
    /// <summary>
    /// Unique identifier for the comment.
    /// </summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Path of the primary document node this comment belongs to.
    /// Used for permission checks (edit access on the document).
    /// For top-level comments, this is the document they annotate.
    /// For replies, this is the original document path (not the parent comment).
    /// </summary>
    [Browsable(false)]
    public string? PrimaryNodePath { get; init; } = string.Empty;

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
    /// Status of the comment (Active or Resolved).
    /// </summary>
    [Browsable(false)]
    public CommentStatus Status { get; init; } = CommentStatus.Active;

    /// <summary>
    /// Child reply node IDs. Parent comment tracks and keeps up to date.
    /// Same pattern as Thread.Messages — updated via workspace.UpdateMeshNode()
    /// when a reply is created.
    /// </summary>
    [Browsable(false)]
    public ImmutableList<string> Replies { get; init; } = [];
}

/// <summary>
/// Represents a tracked change (insertion or deletion) in a collaborative document.
/// Tracked changes are satellite entities — permissions delegate to the primary document node.
/// The actual text content stays in the markers within the markdown;
/// this entity only tracks location and metadata.
/// </summary>
public record TrackedChange
{
    /// <summary>
    /// Unique identifier for the change.
    /// </summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Path of the primary document node this change belongs to.
    /// Used for permission checks (satellite permission delegation).
    /// </summary>
    [Browsable(false)]
    public string? PrimaryNodePath { get; init; }

    /// <summary>
    /// Type of change (Insertion or Deletion).
    /// </summary>
    [Browsable(false)]
    public TrackedChangeType ChangeType { get; init; }

    /// <summary>
    /// Author of the change.
    /// </summary>
    [Browsable(false)]
    public string Author { get; init; } = string.Empty;

    /// <summary>
    /// When the change was made.
    /// </summary>
    [Browsable(false)]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Status of the change.
    /// </summary>
    [Browsable(false)]
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
