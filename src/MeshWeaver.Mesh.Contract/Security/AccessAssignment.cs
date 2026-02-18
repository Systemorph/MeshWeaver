namespace MeshWeaver.Mesh.Security;

/// <summary>
/// View model representing a single role assignment at a specific level in the hierarchy.
/// Used by the Access Control management UI to show inherited vs. local assignments.
/// </summary>
/// <param name="UserId">The user's ObjectId</param>
/// <param name="DisplayName">The user's display name (may be null)</param>
/// <param name="RoleId">The role identifier (e.g., "Admin", "Editor")</param>
/// <param name="SourcePath">Where this assignment comes from: "" for global, "ACME" for namespace-specific</param>
/// <param name="Denied">True if this is a deny override, false if granted</param>
/// <param name="IsLocal">True if this assignment is from the current node's own partition</param>
public record AccessAssignment(
    string UserId,
    string? DisplayName,
    string RoleId,
    string SourcePath,
    bool Denied,
    bool IsLocal
);
