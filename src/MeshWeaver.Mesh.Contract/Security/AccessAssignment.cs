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
/// Caps effective permissions at a namespace scope for ALL users (including Admins).
/// Each permission can be individually switched off (false = denied at this scope and below).
/// null = inherit from parent scope (default = allowed).
/// Multiple policies accumulate: parent denials carry to descendants.
/// Stored as a MeshNode with nodeType = "PartitionAccessPolicy",
/// id = "_Policy", namespace = target scope.
/// </summary>
public record PartitionAccessPolicy
{
    /// <summary>false = deny Read at this scope and below. null = inherit (default: allowed).</summary>
    public bool? Read { get; init; }

    /// <summary>false = deny Create at this scope and below. null = inherit (default: allowed).</summary>
    public bool? Create { get; init; }

    /// <summary>false = deny Update at this scope and below. null = inherit (default: allowed).</summary>
    public bool? Update { get; init; }

    /// <summary>false = deny Delete at this scope and below. null = inherit (default: allowed).</summary>
    public bool? Delete { get; init; }

    /// <summary>false = deny Comment at this scope and below. null = inherit (default: allowed).</summary>
    public bool? Comment { get; init; }

    /// <summary>
    /// When true, role assignments from ancestor scopes are discarded at this
    /// namespace boundary. Only roles assigned at this scope or deeper take effect
    /// (still subject to permission caps).
    /// </summary>
    public bool BreaksInheritance { get; init; }

    /// <summary>
    /// Computes the permission cap mask from individual switches.
    /// Permissions set to false are removed; null (inherit) and true are kept.
    /// </summary>
    public Permission GetPermissionCap()
    {
        var cap = Permission.All;
        if (Read == false) cap &= ~Permission.Read;
        if (Create == false) cap &= ~Permission.Create;
        if (Update == false) cap &= ~Permission.Update;
        if (Delete == false) cap &= ~Permission.Delete;
        if (Comment == false) cap &= ~Permission.Comment;
        return cap;
    }
}
