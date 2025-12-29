using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Configures default security settings for a NodeType.
/// Applied to all instances of that NodeType unless overridden at node level.
/// </summary>
public record RoleConfiguration
{
    /// <summary>
    /// The NodeType this configuration applies to.
    /// </summary>
    [Key]
    public required string NodeType { get; init; }

    /// <summary>
    /// Available roles for this NodeType.
    /// Maps role ID to Role definition.
    /// </summary>
    public IReadOnlyDictionary<string, Role> Roles { get; init; } =
        new Dictionary<string, Role>();

    /// <summary>
    /// Whether this NodeType's nodes are publicly accessible.
    /// When true, anonymous users can read nodes of this type.
    /// </summary>
    public bool IsPublic { get; init; } = false;

    /// <summary>
    /// Permissions granted to anonymous (unauthenticated) users.
    /// Only applies when IsPublic is true.
    /// </summary>
    public Permission AnonymousPermissions { get; init; } = Permission.None;

    /// <summary>
    /// Whether nodes of this type inherit permissions from parent nodes.
    /// When true, permissions assigned on parent paths apply to child nodes.
    /// </summary>
    public bool InheritFromParent { get; init; } = true;
}
