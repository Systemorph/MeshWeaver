using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Security configuration for a specific node instance.
/// Overrides NodeType defaults when specified.
/// </summary>
public record NodeSecurityConfiguration
{
    /// <summary>
    /// The node path this configuration applies to.
    /// </summary>
    [Key]
    public required string NodePath { get; init; }

    /// <summary>
    /// Custom roles for this specific node (overrides NodeType defaults).
    /// If null, NodeType defaults are used.
    /// </summary>
    public IReadOnlyDictionary<string, Role>? CustomRoles { get; init; }

    /// <summary>
    /// Override public accessibility for this node.
    /// If null, NodeType default is used.
    /// </summary>
    public bool? IsPublicOverride { get; init; }

    /// <summary>
    /// Override anonymous permissions for this node.
    /// If null, NodeType default is used.
    /// </summary>
    public Permission? AnonymousPermissionsOverride { get; init; }

    /// <summary>
    /// Override inheritance behavior for this node.
    /// If null, NodeType default is used.
    /// </summary>
    public bool? InheritFromParentOverride { get; init; }

    /// <summary>
    /// Whether this node's permissions can be inherited by children.
    /// When false, children do not inherit permissions from this node.
    /// </summary>
    public bool AllowChildInheritance { get; init; } = true;
}
