using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
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
    /// The exact text that was highlighted when the comment was created. Kept for display (the quote
    /// shown on the card) and as a sanity check / fallback when re-anchoring.
    /// </summary>
    [Browsable(false)]
    public string? HighlightedText { get; init; }

    /// <summary>
    /// The document <see cref="MeshNode.Version"/> the capture (<see cref="Start"/>/<see cref="Length"/>/
    /// <see cref="AnchorText"/>) was taken against. The comment is NOT stored inside the document text;
    /// the inline highlight is re-derived. While the document is still at this version the captured
    /// offsets are exact; once it is ahead the range is recomputed by diffing <see cref="AnchorText"/>
    /// against the current text (see AnchorMath). Zero for page-level comments.
    /// </summary>
    [Browsable(false)]
    public long Version { get; init; }

    /// <summary>
    /// Start offset of the highlight in the document's clean text at <see cref="Version"/>.
    /// <c>-1</c> when the comment has no inline anchor (page-level).
    /// </summary>
    [Browsable(false)]
    public int Start { get; init; } = -1;

    /// <summary>
    /// Length (in characters) of the highlight at <see cref="Version"/>.
    /// </summary>
    [Browsable(false)]
    public int Length { get; init; }

    /// <summary>
    /// The document's clean text at <see cref="Version"/> — the "anchor" the capture was taken
    /// against. Diffed against the current text to recompute the effective range when the document
    /// has moved on. Null for page-level comments.
    /// </summary>
    [Browsable(false)]
    public string? AnchorText { get; init; }

    /// <summary>
    /// Effective start of the highlight in the CURRENT document text, recomputed at display time.
    /// Not persisted — derived from the capture via the version delta. <c>-1</c> when not resolved.
    /// </summary>
    [Browsable(false)]
    [JsonIgnore]
    public int EffectiveStart { get; init; } = -1;

    /// <summary>
    /// Effective end (exclusive) of the highlight in the CURRENT document text. Not persisted.
    /// </summary>
    [Browsable(false)]
    [JsonIgnore]
    public int EffectiveEnd { get; init; } = -1;

    /// <summary>
    /// The document version the <see cref="EffectiveStart"/>/<see cref="EffectiveEnd"/> were resolved
    /// against (the current version at display time). Not persisted.
    /// </summary>
    [Browsable(false)]
    [JsonIgnore]
    public long EffectiveVersion { get; init; }

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
