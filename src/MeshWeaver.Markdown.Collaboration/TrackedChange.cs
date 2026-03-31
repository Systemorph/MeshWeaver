using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Markdown.Collaboration;

/// <summary>
/// A suggested edit that can be accepted or rejected.
/// Track changes are embedded in the markdown content as markers:
/// - Insertions: &lt;!--insert:MarkerId--&gt;new text&lt;!--/insert:MarkerId--&gt;
/// - Deletions: &lt;!--delete:MarkerId--&gt;deleted text&lt;!--/delete:MarkerId--&gt;
/// </summary>
public record TrackedChange
{
    /// <summary>
    /// Unique identifier for this tracked change.
    /// </summary>
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The document this change belongs to.
    /// </summary>
    public string DocumentId { get; init; } = string.Empty;

    /// <summary>
    /// The marker ID embedded in the markdown content.
    /// Links to &lt;!--insert:MarkerId--&gt; or &lt;!--delete:MarkerId--&gt;
    /// </summary>
    public string MarkerId { get; init; } = string.Empty;

    /// <summary>
    /// Who suggested this change.
    /// </summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>
    /// When this change was suggested.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The type of change (insertion, deletion, or replacement).
    /// </summary>
    public TrackedChangeType Type { get; init; }

    /// <summary>
    /// For insertions/replacements: the text being inserted.
    /// </summary>
    public string InsertedText { get; init; } = string.Empty;

    /// <summary>
    /// For deletions/replacements: the text being deleted.
    /// </summary>
    public string DeletedText { get; init; } = string.Empty;

    /// <summary>
    /// Current review status of this change.
    /// </summary>
    public TrackedChangeStatus Status { get; init; } = TrackedChangeStatus.Pending;

    /// <summary>
    /// Who accepted or rejected this change.
    /// </summary>
    public string? ReviewedBy { get; init; }

    /// <summary>
    /// When this change was reviewed.
    /// </summary>
    public DateTimeOffset? ReviewedAt { get; init; }
}

/// <summary>
/// Types of tracked changes.
/// </summary>
public enum TrackedChangeType
{
    /// <summary>
    /// New text is being added.
    /// </summary>
    Insertion,

    /// <summary>
    /// Existing text is being removed.
    /// </summary>
    Deletion,

    /// <summary>
    /// Text is being replaced (both deletion and insertion).
    /// </summary>
    Replacement
}

/// <summary>
/// Review status of a tracked change.
/// </summary>
public enum TrackedChangeStatus
{
    /// <summary>
    /// Change is awaiting review.
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
