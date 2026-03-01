using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Content type for AccessAssignment mesh nodes.
/// Maps a subject (User or Group) to one or more roles at a specific scope.
/// The scope is determined by the node's namespace in the mesh hierarchy.
/// Node ID = {Subject}_Access, so one node per subject per scope.
/// </summary>
public record AccessAssignment
{
    /// <summary>Subject identifier (User or Group path) for this assignment.</summary>
    [MeshNode("namespace:User nodeType:User", "namespace:{node.namespace} nodeType:Group scope:selfAndAncestors")]
    public string AccessObject { get; init; } = "";

    /// <summary>Optional display name for the subject.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Role assignments for this subject at this scope.</summary>
    [MeshNodeCollection("namespace:{node.namespace} nodeType:Role scope:selfAndAncestors")]
    public IReadOnlyList<RoleAssignment> Roles { get; init; } = [];
}

/// <summary>
/// A single role assignment within an AccessAssignment.
/// </summary>
public record RoleAssignment
{
    /// <summary>The role identifier (e.g., "Admin", "Editor", "Viewer").</summary>
    public string Role { get; init; } = "";

    /// <summary>True if this assignment denies rather than grants the role.</summary>
    [UiControl(typeof(SwitchControl))]
    public bool Denied { get; init; }
}

/// <summary>
/// Caps the maximum effective permissions at a namespace scope for ALL users.
/// When present, all users (including Admins) have their permissions
/// masked to MaxPermissions at this scope and all descendants.
/// Stored as a MeshNode with nodeType = "PartitionAccessPolicy",
/// id = "_Policy", namespace = target scope.
/// </summary>
public record PartitionAccessPolicy
{
    /// <summary>
    /// The maximum permissions allowed at this namespace and below.
    /// Effective permissions are masked: result &amp;= MaxPermissions.
    /// E.g., Permission.Read means this namespace is read-only.
    /// </summary>
    public Permission MaxPermissions { get; init; } = Permission.All;

    /// <summary>
    /// When true, role assignments from ancestor scopes are discarded at this
    /// namespace boundary. Only roles assigned at this scope or deeper take effect
    /// (still subject to MaxPermissions cap).
    /// </summary>
    public bool BreaksInheritance { get; init; }
}
