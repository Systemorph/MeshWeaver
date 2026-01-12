using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Well-known user IDs for access control.
/// </summary>
public static class WellKnownUsers
{
    /// <summary>
    /// The "Public" user represents anonymous/unauthenticated access.
    /// Assign roles to "Public" to grant permissions to anonymous users.
    /// </summary>
    public const string Public = "Public";
}

/// <summary>
/// Represents a user's access to a namespace.
/// Stored in {namespace}/Access/{userId}.json for namespace-specific access,
/// or Access/{userId}.json for global access.
/// </summary>
public record UserAccess
{
    /// <summary>
    /// The user's ObjectId (from AccessContext or Person node id).
    /// Use "Public" for anonymous access.
    /// </summary>
    [Key]
    public required string UserId { get; init; }

    /// <summary>
    /// The user's display name (for UI convenience).
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// The list of roles assigned to this user for this namespace.
    /// </summary>
    public IReadOnlyList<UserRole> Roles { get; init; } = [];
}

/// <summary>
/// A role assignment within a UserAccess record.
/// </summary>
public record UserRole
{
    /// <summary>
    /// The role identifier (e.g., "Admin", "Editor", "Viewer").
    /// </summary>
    public required string RoleId { get; init; }

    /// <summary>
    /// When this role assignment was created.
    /// </summary>
    public DateTimeOffset AssignedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Who assigned this role (user ObjectId).
    /// </summary>
    public string? AssignedBy { get; init; }
}
