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
    /// Built-in Editor role with read, create, update, and comment permissions.
    /// </summary>
    public static Role Editor => new()
    {
        Id = "Editor",
        DisplayName = "Editor",
        Description = "Can read, create, update nodes, and comment",
        Permissions = Permission.Read | Permission.Create | Permission.Update | Permission.Comment,
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

    /// <summary>
    /// Built-in Commenter role with read and comment permissions.
    /// Can be assigned to the Public user to enable public commenting.
    /// </summary>
    public static Role Commenter => new()
    {
        Id = "Commenter",
        DisplayName = "Commenter",
        Description = "Can read and comment",
        Permissions = Permission.Read | Permission.Comment,
        IsInheritable = true
    };

    /// <summary>
    /// Built-in Platform Administrator role.
    /// Grants access to platform-level settings (auth providers, admin configuration).
    /// Assigned in the Admin namespace via standard AccessAssignment nodes.
    /// </summary>
    public static Role PlatformAdmin => new()
    {
        Id = "PlatformAdmin",
        DisplayName = "Platform Admin",
        Description = "Full access to platform settings and administration",
        Permissions = Permission.All,
        IsInheritable = true
    };
}
