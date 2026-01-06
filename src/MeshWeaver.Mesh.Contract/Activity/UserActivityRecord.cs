using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh.Activity;

/// <summary>
/// Records a user's access to a mesh node.
/// Stored in partition storage at _activity/{userId}.
/// </summary>
public record UserActivityRecord
{
    /// <summary>
    /// Unique identifier for this activity record.
    /// Format: nodePath with / replaced by _.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// The path of the accessed node.
    /// </summary>
    public string NodePath { get; init; } = string.Empty;

    /// <summary>
    /// The user's ObjectId who accessed the node.
    /// </summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>
    /// Type of access (Read, Write, Delete).
    /// </summary>
    public ActivityType ActivityType { get; init; }

    /// <summary>
    /// When this node was first accessed by this user.
    /// </summary>
    public DateTimeOffset FirstAccessedAt { get; init; }

    /// <summary>
    /// When this node was last accessed by this user.
    /// </summary>
    public DateTimeOffset LastAccessedAt { get; init; }

    /// <summary>
    /// Total number of accesses by this user.
    /// </summary>
    public int AccessCount { get; init; }

    /// <summary>
    /// Name of the node at time of last access (for display).
    /// </summary>
    public string? NodeName { get; init; }

    /// <summary>
    /// NodeType at time of last access (for filtering).
    /// </summary>
    public string? NodeType { get; init; }

    /// <summary>
    /// Namespace of the node (for filtering by namespace).
    /// </summary>
    public string? Namespace { get; init; }
}

/// <summary>
/// Type of activity tracked.
/// </summary>
public enum ActivityType
{
    /// <summary>
    /// User read/viewed a node.
    /// </summary>
    Read,

    /// <summary>
    /// User modified a node.
    /// </summary>
    Write,

    /// <summary>
    /// User deleted a node.
    /// </summary>
    Delete
}
