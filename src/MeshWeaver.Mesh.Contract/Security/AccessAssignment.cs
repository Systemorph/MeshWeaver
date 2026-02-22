namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Content type for AccessAssignment mesh nodes.
/// Maps a subject (User or Group) to one or more roles at a specific scope.
/// The scope is determined by the node's namespace in the mesh hierarchy.
/// Node ID = SubjectId, so one node per subject per scope.
/// </summary>
public record AccessAssignment
{
    /// <summary>Subject identifier (User or Group path) for this assignment.</summary>
    public string SubjectId { get; init; } = "";

    /// <summary>Optional display name for the subject.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Role assignments for this subject at this scope.</summary>
    public IReadOnlyList<RoleAssignment> Roles { get; init; } = [];
}

/// <summary>
/// A single role assignment within an AccessAssignment.
/// </summary>
public record RoleAssignment
{
    /// <summary>The role identifier (e.g., "Admin", "Editor", "Viewer").</summary>
    public string RoleId { get; init; } = "";

    /// <summary>True if this assignment denies rather than grants the role.</summary>
    public bool Denied { get; init; }
}
