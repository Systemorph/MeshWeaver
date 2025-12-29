namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Defines a role with associated permissions.
/// Roles can be system-defined (Admin, Editor, Viewer) or custom.
/// </summary>
public record Role
{
    /// <summary>
    /// Unique role identifier (e.g., "Admin", "Editor", "Viewer", "CustomRole").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable name for display.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Description of what this role allows.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The permissions granted by this role.
    /// </summary>
    public Permission Permissions { get; init; } = Permission.None;

    /// <summary>
    /// Whether this role's permissions are inherited by child nodes.
    /// When true, assigning this role on node "a/b" also grants permissions on "a/b/c", "a/b/c/d", etc.
    /// </summary>
    public bool IsInheritable { get; init; } = true;

    /// <summary>
    /// Built-in Administrator role with full permissions.
    /// </summary>
    public static Role Admin => new()
    {
        Id = "Admin",
        DisplayName = "Administrator",
        Description = "Full access to all operations",
        Permissions = Permission.All,
        IsInheritable = true
    };

    /// <summary>
    /// Built-in Editor role with read, create, and update permissions.
    /// </summary>
    public static Role Editor => new()
    {
        Id = "Editor",
        DisplayName = "Editor",
        Description = "Can read, create, and update nodes",
        Permissions = Permission.Read | Permission.Create | Permission.Update,
        IsInheritable = true
    };

    /// <summary>
    /// Built-in Viewer role with read-only permissions.
    /// </summary>
    public static Role Viewer => new()
    {
        Id = "Viewer",
        DisplayName = "Viewer",
        Description = "Read-only access",
        Permissions = Permission.Read,
        IsInheritable = true
    };
}
