using System.Collections.Immutable;

namespace MeshWeaver.Markdown.Collaboration;

/// <summary>
/// Base record for text editing operations used in collaborative editing.
/// Operations are designed to support Operational Transformation (OT).
/// </summary>
public abstract record TextOperation
{
    /// <summary>
    /// Unique identifier for this operation.
    /// </summary>
    public string OperationId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The document this operation applies to.
    /// </summary>
    public string DocumentId { get; init; } = string.Empty;

    /// <summary>
    /// The document version this operation was created against.
    /// Used for OT conflict detection and transformation.
    /// </summary>
    public long BaseVersion { get; init; }

    /// <summary>
    /// The user who created this operation.
    /// </summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>
    /// When this operation was created.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Insert text at a specific position in the document.
/// </summary>
public record InsertOperation : TextOperation
{
    /// <summary>
    /// Character position where text should be inserted (0-based).
    /// </summary>
    public int Position { get; init; }

    /// <summary>
    /// The text to insert.
    /// </summary>
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// Delete characters starting at a position.
/// </summary>
public record DeleteOperation : TextOperation
{
    /// <summary>
    /// Character position where deletion starts (0-based).
    /// </summary>
    public int Position { get; init; }

    /// <summary>
    /// Number of characters to delete.
    /// </summary>
    public int Length { get; init; }

    /// <summary>
    /// The text that was deleted (for undo support and conflict resolution).
    /// </summary>
    public string DeletedText { get; init; } = string.Empty;
}

/// <summary>
/// Composite operation for atomic multi-part changes (e.g., replace = delete + insert).
/// All operations in a composite are applied atomically.
/// </summary>
public record CompositeOperation : TextOperation
{
    /// <summary>
    /// The operations to apply, in order.
    /// </summary>
    public ImmutableList<TextOperation> Operations { get; init; } = ImmutableList<TextOperation>.Empty;
}

/// <summary>
/// No-op operation used for testing and synchronization acknowledgment.
/// </summary>
public record NoOpOperation : TextOperation;
