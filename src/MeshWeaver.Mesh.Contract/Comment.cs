using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

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
}
