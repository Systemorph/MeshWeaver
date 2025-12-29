using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Assigns a role to a user for a specific node or globally.
/// </summary>
public record RoleAssignment
{
    /// <summary>
    /// Unique identifier for this assignment.
    /// </summary>
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The user's ObjectId (from AccessContext).
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// The role being assigned.
    /// </summary>
    public required string RoleId { get; init; }

    /// <summary>
    /// The node path this assignment applies to.
    /// If null, applies globally to all nodes.
    /// </summary>
    public string? NodePath { get; init; }

    /// <summary>
    /// When this assignment was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Who created this assignment (user ObjectId).
    /// </summary>
    public string? CreatedBy { get; init; }
}
