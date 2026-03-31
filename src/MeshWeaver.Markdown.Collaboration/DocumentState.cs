using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Markdown.Collaboration;

/// <summary>
/// Metadata for a collaboratively edited markdown document.
/// The actual content is stored as a string in MeshNode.Content.
/// </summary>
public record MarkdownDocumentState
{
    /// <summary>
    /// Document identifier (matches the node path).
    /// </summary>
    [Key]
    public string DocumentId { get; init; } = string.Empty;

    /// <summary>
    /// Current document version for OT conflict detection.
    /// Incremented with each applied operation.
    /// </summary>
    public long Version { get; init; }

    /// <summary>
    /// Vector clock for CRDT-style ordering (userId -> logicalClock).
    /// Used for additional conflict resolution in distributed scenarios.
    /// </summary>
    public ImmutableDictionary<string, long> VectorClock { get; init; }
        = ImmutableDictionary<string, long>.Empty;

    /// <summary>
    /// Active editing sessions for presence awareness.
    /// </summary>
    public ImmutableList<EditingSession> ActiveSessions { get; init; }
        = ImmutableList<EditingSession>.Empty;

    /// <summary>
    /// When the document was last modified.
    /// </summary>
    public DateTimeOffset LastModified { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Represents an active editing session for presence awareness.
/// </summary>
public record EditingSession
{
    /// <summary>
    /// Unique session identifier.
    /// </summary>
    public string SessionId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The user in this session.
    /// </summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>
    /// Display name for the user.
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Current cursor position in the document.
    /// </summary>
    public int CursorPosition { get; init; }

    /// <summary>
    /// Selection start (if any).
    /// </summary>
    public int? SelectionStart { get; init; }

    /// <summary>
    /// Selection end (if any).
    /// </summary>
    public int? SelectionEnd { get; init; }

    /// <summary>
    /// Color assigned to this user for visual identification.
    /// </summary>
    public string Color { get; init; } = "#0078d4";

    /// <summary>
    /// When this session was last active.
    /// </summary>
    public DateTimeOffset LastActivity { get; init; } = DateTimeOffset.UtcNow;
}
